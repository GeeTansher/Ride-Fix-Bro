using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Controllers;

[ApiController, Authorize(Roles = "Admin"), Route("api/admin/bikes/{bikeId:int:min(1)}/manual")]
public sealed class AdminManualsController(RideFixBroDbContext database, VectorDbService vectors,
    ILogger<AdminManualsController> logger) : ControllerBase
{
    // POST multipart/form-data: file=<PDF>, skipPages=<count>. bikeId is MasterBikes.Id, NOT UserBikes.Id.
    // Bearer token must resolve to SQL role Admin. ManualKey form se nahi, selected catalog row se aayegi.
    // Scanned PDFs need OCR first; limits: 20 MiB, 1000 pages, 2000 chunks.
    // Qdrant_Vector_DB:ManualCollection is required with no fallback; embeddings stay 3072-dimensional.
    // Upload creates exact-match payload indexes + one active-revision metadata point per ManualKey.
    // Untagged legacy points are NOT guessed/deleted; only this key's previous revisions are removed.
    // Multipart overhead allowed separately; actual file is still capped at exactly 20 MiB.
    [HttpPost, Consumes("multipart/form-data")]
    [RequestSizeLimit(VectorDbService.MaxPdfBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = VectorDbService.MaxPdfBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(int bikeId, [FromForm] ManualUploadRequest request, CancellationToken token)
    {
        var bike = await database.MasterBikes.AsNoTracking().SingleOrDefaultAsync(row => row.Id == bikeId, token);
        if (bike is null) return NotFound(new { Error = "Catalog bike not found." });
        if (string.IsNullOrWhiteSpace(bike.ManualKey))
            return Conflict(new { Error = "Assign a verified ManualKey to the catalog entry before upload." });
        if (request.File.Length <= 0 || request.File.Length > VectorDbService.MaxPdfBytes)
            return StatusCode(413, new { Error = "Upload a non-empty PDF up to 20 MiB." });
        using var buffer = new MemoryStream();
        await request.File.CopyToAsync(buffer, token);
        try
        {
            // await using DisposeAsync ko implicitly await karta hai; upload finish/fail hone par lock release hoga.
            await using var publication = new ManualPublicationLock(database);
            await publication.AcquireAsync(token);
            return Ok(await vectors.ReplaceManualAsync(buffer.ToArray(), bike.ManualKey, request.SkipPages, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ChatInputException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            logger.LogError(ex, "Manual replacement failed for catalog bike {BikeId}.", bikeId);
            return StatusCode(503, new { Error = "Manual upload/publication or old-chunk cleanup failed. Retry the complete upload; do not assume replacement finished." });
        }
    }
}
