using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/bikes"), Consumes("multipart/form-data")]
[RequestSizeLimit(VectorDbService.MaxPdfBytes + 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = VectorDbService.MaxPdfBytes + 1024 * 1024)]
public sealed class AdminManualsController(RideFixBroDbContext database, IManualPublisher publisher,
    ILogger<AdminManualsController> logger) : ControllerBase
{
    // Official collection is configured only on the backend:
    // Qdrant_Vector_DB:ManualCollection (Azure setting: Qdrant_Vector_DB__ManualCollection). No fallback.
    // POST form fields: make, model, year, manualKey, file (PDF), skipPages (default 0).
    // Same make/model/year + same key is a re-upload. A new catalog row is saved only after publication succeeds.
    [HttpPost]
    public async Task<IActionResult> PublishBike([FromForm] PublishBikeRequest request, CancellationToken token)
    {
        var make = request.Make.Trim();
        var model = request.Model.Trim();
        var manualKey = request.ManualKey.Trim();
        var pdf = await ReadPdfAsync(request.File, token);
        try
        {
            await using var publication = new ManualPublicationLock(database);
            await publication.AcquireAsync(token);
            var bike = await database.MasterBikes.SingleOrDefaultAsync(
                row => row.Make == make && row.Model == model && row.Year == request.Year, token);
            if (bike?.ManualKey is { Length: > 0 } existingKey && existingKey != manualKey)
                throw new ChatInputException("This catalog bike already has a different ManualKey. Use its existing key to re-upload.", 409);
            if (bike is null && await database.MasterBikes.AnyAsync(row => row.ManualKey == manualKey, token))
                throw new ChatInputException("This ManualKey belongs to another catalog bike. Choose a distinct key.", 409);

            var result = await publisher.ReplaceManualAsync(pdf, manualKey, request.SkipPages, token);
            var created = bike is null;
            if (bike is null)
            {
                bike = new MasterBike { Make = make, Model = model, Year = request.Year, ManualKey = manualKey };
                database.MasterBikes.Add(bike);
            }
            else bike.ManualKey = manualKey;
            // SQL and Qdrant are separate systems. If this save fails, retry the same complete form;
            // the published PDF may exist, but no successful catalog-creation response is sent.
            await database.SaveChangesAsync(token);
            var response = new BikePublicationResponse(
                new BikeResponse(bike.Id, bike.Make, bike.Model, bike.Year), result.ManualKey, result.Chunks, result.SkippedPages);
            return StatusCode(created ? 201 : 200, response);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ChatInputException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            logger.LogError(ex, "Official bike/manual publication failed.");
            return Unavailable();
        }
    }

    // Existing catalog ID upload remains available for API clients; it cannot create a new bike.
    [HttpPost("{bikeId:int:min(1)}/manual")]
    public async Task<IActionResult> Upload(int bikeId, [FromForm] ManualUploadRequest request, CancellationToken token)
    {
        var pdf = await ReadPdfAsync(request.File, token);
        try
        {
            await using var publication = new ManualPublicationLock(database);
            await publication.AcquireAsync(token);
            var bike = await database.MasterBikes.AsNoTracking().SingleOrDefaultAsync(row => row.Id == bikeId, token);
            if (bike is null) throw new ChatInputException("Catalog bike not found.", 404);
            if (string.IsNullOrWhiteSpace(bike.ManualKey))
                throw new ChatInputException("Assign a verified ManualKey before upload.", 409);
            return Ok(await publisher.ReplaceManualAsync(pdf, bike.ManualKey, request.SkipPages, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ChatInputException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            logger.LogError(ex, "Manual replacement failed for catalog bike {BikeId}.", bikeId);
            return Unavailable();
        }
    }

    private static async Task<byte[]> ReadPdfAsync(IFormFile file, CancellationToken token)
    {
        if (file.Length <= 0 || file.Length > VectorDbService.MaxPdfBytes)
            throw new ChatInputException("Upload a non-empty PDF up to 20 MiB.", 413);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, token);
        return buffer.ToArray();
    }

    private ObjectResult Unavailable() => StatusCode(503, new
    {
        Error = "Publication did not finish. PDF publishing, cleanup or catalog save failed. Retry the same complete form; do not assume the bike was created."
    });
}
