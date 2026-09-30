using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Common;
using RideFixBro.API.Models.ManualPublishModels;
using RideFixBro.API.Services;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish;
using System.Globalization;
using System.Security.Claims;

namespace RideFixBro.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/bikes"), Consumes("multipart/form-data")]
[RequestSizeLimit(VectorDbService.MaxPdfBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = VectorDbService.MaxPdfBytes + 1024 * 1024)]
public sealed class AdminManualsController(ManualPublicationJobsService jobs) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);

    // Qdrant_Vector_DB:ManualCollection selects the official collection (Azure: Qdrant_Vector_DB__ManualCollection).
    // 202 means the text/job is durably accepted, NOT that Qdrant or the catalog is already published.
    [HttpPost]
    public async Task<IActionResult> PublishBike([FromForm] PublishBikeRequest request, CancellationToken token)
    {
        var chunks = await ExtractAsync(request, token);
        var job = await jobs.SubmitAsync(UserId,
            new(request.Make, request.Model, request.Year, request.ManualKey, request.SkipPages), chunks, token);
        return AcceptedAtAction(nameof(ManualPublicationJobsController.Get), "ManualPublicationJobs", new { id = job.Id }, job);
    }

    [HttpPost("{bikeId:int:min(1)}/manual")]
    public async Task<IActionResult> Upload(int bikeId, [FromForm] ManualUploadRequest request, CancellationToken token)
    {
        var spec = await jobs.ExistingBikeAsync(bikeId, request.SkipPages, token);
        var chunks = await ExtractAsync(request, token);
        var job = await jobs.SubmitAsync(UserId, spec, chunks, token);
        return AcceptedAtAction(nameof(ManualPublicationJobsController.Get), "ManualPublicationJobs", new { id = job.Id }, job);
    }

    private static async Task<List<string>> ExtractAsync(ManualUploadRequest request, CancellationToken token)
    {
        if (request.File.Length <= 0 || request.File.Length > VectorDbService.MaxPdfBytes)
            throw new ChatInputException("Upload a non-empty PDF up to 20 MiB.", 413);
        using var buffer = new MemoryStream();
        await request.File.CopyToAsync(buffer, token);
        // Only upload + bounded text preparation happen within HTTP; Gemini/Qdrant work belongs to the worker.
        return VectorDbService.ExtractChunks(buffer.ToArray(), request.SkipPages, token);
    }
}
