using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Common;
using RideFixBro.API.Models.ManualPublishModels;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Interface;
using RideFixBro.Data.Entities;
using System.Text;
using System.Text.Json;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish;

public sealed class ManualPublicationJobsService(RideFixBroDbContext database, IManualPublisher publisher,
    ManualPublicationQueue queue, IConfiguration configuration, ILogger<ManualPublicationJobsService> logger)
{
    private const int MaxActiveJobs = 3;
    private const int MaxStoredTextBytes = 16 * 1024 * 1024;

    public async Task<ManualPublicationJobResponse> SubmitAsync(int userId, ManualPublicationSpec input,
        IReadOnlyList<string> chunks, CancellationToken token)
    {
        var spec = input with { Make = input.Make.Trim(), Model = input.Model.Trim(), ManualKey = input.ManualKey.Trim() };
        if (userId <= 0 || string.IsNullOrWhiteSpace(spec.Make) || spec.Make.Length > 100 ||
            string.IsNullOrWhiteSpace(spec.Model) || spec.Model.Length > 100 ||
            string.IsNullOrWhiteSpace(spec.ManualKey) || spec.ManualKey.Length > 128 ||
            spec.Year is < 1900 or > 2100 || spec.SkipPages is < 0 or > 999)
            throw new ChatInputException("Valid make, model, year, ManualKey and skipPages are required.");
        ValidateText(chunks);
        var json = JsonSerializer.Serialize(chunks);
        if (Encoding.Unicode.GetByteCount(json) > MaxStoredTextBytes)
            throw new ChatInputException("Extracted text exceeds the 16 MiB publication-job limit.", 413);
        var collection = CollectionName();
        if (!await queue.SubmissionGate.WaitAsync(0, token))
            throw new ChatInputException("Another publication is being submitted. Try again shortly.", 409);
        try
        {
            // Short submission lock bounds queued text; the worker uses a different, long-lived publication lock.
            await using var submission = new ManualPublicationLock(database, "RideFix.ManualPublicationQueue");
            await submission.AcquireAsync(token);
            if (await database.ManualPublicationJobs.CountAsync(job => job.CompletedAt == null, token) >= MaxActiveJobs)
                throw new ChatInputException("The publication queue is full. Wait for an active job to finish.", 409);
            if (await database.ManualPublicationJobs.AnyAsync(job => job.CompletedAt == null &&
                job.CollectionName == collection && (job.ManualKey == spec.ManualKey ||
                    (job.Make == spec.Make && job.Model == spec.Model && job.Year == spec.Year)), token))
                throw new ChatInputException("This bike/manual already has an active publication. Check its job status.", 409);

            var bike = await ValidateCatalogAsync(spec, null, token);
            var now = DateTime.UtcNow;
            var row = new ManualPublicationJob
            {
                Id = Guid.NewGuid(), SubmittedByUserId = userId,
                Make = spec.Make, Model = spec.Model, Year = spec.Year, ManualKey = spec.ManualKey,
                CollectionName = collection, SkipPages = spec.SkipPages, ChunksJson = json,
                TotalChunks = chunks.Count, CompletedChunks = 0, Status = ManualPublicationStatus.Queued,
                BikeId = bike?.Id, CreatedAt = now, UpdatedAt = now
            };
            database.ManualPublicationJobs.Add(row);
            try { await database.SaveChangesAsync(token); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new ChatInputException("An active publication already exists for this manual. Check job status.", 409);
            }
            var response = Response(row);
            queue.Wake();
            return response;
        }
        finally { queue.SubmissionGate.Release(); }
    }

    public async Task<ManualPublicationSpec> ExistingBikeAsync(int bikeId, int skipPages, CancellationToken token)
    {
        var bike = await database.MasterBikes.AsNoTracking().SingleOrDefaultAsync(row => row.Id == bikeId, token)
            ?? throw new ChatInputException("Catalog bike not found.", 404);
        if (string.IsNullOrWhiteSpace(bike.ManualKey))
            throw new ChatInputException("Assign a verified ManualKey before upload.", 409);
        return new(bike.Make, bike.Model, bike.Year, bike.ManualKey, skipPages);
    }

    public async Task<ManualPublicationJobResponse> GetAsync(int userId, Guid id, CancellationToken token)
    {
        var result = await Responses(database.ManualPublicationJobs.AsNoTracking()
                .Where(job => job.SubmittedByUserId == userId && job.Id == id)).SingleOrDefaultAsync(token)
            ?? throw new ChatInputException("Publication job not found.", 404);
        if (result.CompletedAt is null) queue.Wake();
        return result;
    }

    public async Task<List<ManualPublicationJobResponse>> ListAsync(int userId, CancellationToken token)
    {
        var result = await Responses(database.ManualPublicationJobs.AsNoTracking()
                .Where(job => job.SubmittedByUserId == userId)
                .OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id).Take(20)).ToListAsync(token);
        if (result.Any(job => job.CompletedAt is null)) queue.Wake();
        return result;
    }

    // Called only by the hosted worker, in its own DI scope with the host shutdown token.
    internal async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        await using var publication = new ManualPublicationLock(database);
        await publication.AcquireAsync(token);
        var job = await database.ManualPublicationJobs.Include(row => row.SubmittedByUser).ThenInclude(user => user.Role)
            .Where(row => row.CompletedAt == null).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
            .FirstOrDefaultAsync(token);
        if (job is null) return false;
        try
        {
            if (job.SubmittedByUser.Role.RoleName != "Admin")
                throw new ChatInputException("The submitting account no longer has Admin permission.", 403);
            if (job.CollectionName != CollectionName())
                throw new ChatInputException("The configured manual collection changed. Submit a new job for the current collection.", 409);
            var chunks = JsonSerializer.Deserialize<List<string>>(job.ChunksJson ?? "")
                ?? throw new InvalidOperationException("Stored publication text is missing.");
            ValidateText(chunks);
            if (chunks.Count != job.TotalChunks || job.CompletedChunks < 0 || job.CompletedChunks > chunks.Count)
                throw new InvalidOperationException("Stored publication checkpoint is invalid.");
            var spec = new ManualPublicationSpec(job.Make, job.Model, job.Year, job.ManualKey, job.SkipPages);
            var bike = await ValidateCatalogAsync(spec, job.BikeId, token);
            job.Status = ManualPublicationStatus.Processing;
            job.Error = null;
            job.UpdatedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(token);
            logger.LogInformation("Processing manual job {JobId} from chunk {Completed} of {Total}.",
                job.Id, job.CompletedChunks, job.TotalChunks);

            await publisher.PublishAsync(new(job.Id, job.CollectionName, job.ManualKey, chunks, job.CompletedChunks),
                async (completed, progressToken) =>
                {
                    // A connection retry must not silently continue a worker whose session-level lock was lost.
                    await publication.EnsureHeldAsync(progressToken);
                    if (completed == job.TotalChunks && !await database.ManualPublicationJobs.AsNoTracking()
                        .AnyAsync(row => row.Id == job.Id && row.CompletedAt == null, progressToken))
                        throw new InvalidOperationException("Publication job is already terminal.");
                    if (completed < job.CompletedChunks || completed > job.TotalChunks)
                        throw new InvalidOperationException("Publisher returned an invalid checkpoint.");
                    job.CompletedChunks = completed;
                    job.UpdatedAt = DateTime.UtcNow;
                    await database.SaveChangesAsync(progressToken);
                }, token);
            token.ThrowIfCancellationRequested();
            await publication.EnsureHeldAsync(token);
            if (job.CompletedChunks != job.TotalChunks)
                throw new InvalidOperationException("Publisher finished without saving all chunk checkpoints.");

            if (bike is null)
            {
                bike = new MasterBike { Make = job.Make, Model = job.Model, Year = job.Year, ManualKey = job.ManualKey };
                database.MasterBikes.Add(bike);
            }
            else bike.ManualKey = job.ManualKey;
            job.Bike = bike;
            job.Status = ManualPublicationStatus.Succeeded;
            job.CompletedAt = job.UpdatedAt = DateTime.UtcNow;
            job.ChunksJson = null;
            // Catalog result + terminal job state are one SQL save, after Qdrant publication/cleanup.
            await database.SaveChangesAsync(token);
            logger.LogInformation("Manual job {JobId} completed for bike {BikeId}.", job.Id, job.BikeId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Leave source/checkpoint intact: startup/status polling wakes recovery after host shutdown.
            logger.LogInformation("Manual job {JobId} interrupted by host shutdown; checkpoint retained.", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            logger.LogError(ex, "Manual job {JobId} failed.", job.Id);
            // If another worker can now own this job, leave its durable state alone and let the outer loop recover.
            await publication.EnsureHeldAsync(token);
            var id = job.Id;
            database.ChangeTracker.Clear();
            var saved = await database.ManualPublicationJobs.SingleAsync(row => row.Id == id, token);
            if (saved.CompletedAt is null)
            {
                var error = ex is ChatInputException input ? input.Message :
                    "Publication failed. Manual activation, cleanup or catalog save may be incomplete; check server logs before re-uploading.";
                saved.Status = ManualPublicationStatus.Failed;
                saved.Error = error.Length > 1000 ? error[..1000] : error;
                saved.CompletedAt = saved.UpdatedAt = DateTime.UtcNow;
                saved.ChunksJson = null;
                await database.SaveChangesAsync(token);
            }
        }
        return true;
    }

    private string CollectionName()
    {
        var name = configuration["Qdrant_Vector_DB:ManualCollection"];
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
            throw new InvalidOperationException("A manual collection name of 1-255 characters is required.");
        return name;
    }

    private static void ValidateText(IReadOnlyList<string> chunks)
    {
        if (chunks.Count is < 1 or > 2000 || chunks.Any(string.IsNullOrWhiteSpace))
            throw new ChatInputException("Publication requires between 1 and 2000 non-empty text chunks.");
    }

    private async Task<MasterBike?> ValidateCatalogAsync(ManualPublicationSpec spec, int? expectedBikeId, CancellationToken token)
    {
        var bike = await database.MasterBikes.SingleOrDefaultAsync(
            row => row.Make == spec.Make && row.Model == spec.Model && row.Year == spec.Year, token);
        if (expectedBikeId.HasValue && bike?.Id != expectedBikeId)
            throw new ChatInputException("The catalog bike changed while publication was queued.", 409);
        if (bike?.ManualKey is { Length: > 0 } key && key != spec.ManualKey)
            throw new ChatInputException("This catalog bike has a different ManualKey. Use its existing key.", 409);
        if (bike is null && await database.MasterBikes.AnyAsync(row => row.ManualKey == spec.ManualKey, token))
            throw new ChatInputException("This ManualKey belongs to another catalog bike.", 409);
        return bike;
    }

    // Status reads deliberately exclude ChunksJson.
    private static IQueryable<ManualPublicationJobResponse> Responses(IQueryable<ManualPublicationJob> jobs) =>
        jobs.Select(job => new ManualPublicationJobResponse(job.Id, job.Status, job.Make, job.Model, job.Year,
                job.ManualKey, job.CollectionName, job.SkipPages, job.TotalChunks, job.CompletedChunks,
                job.BikeId, job.Error, job.CreatedAt, job.UpdatedAt, job.CompletedAt));

    private static ManualPublicationJobResponse Response(ManualPublicationJob job) =>
        new(job.Id, job.Status, job.Make, job.Model, job.Year, job.ManualKey, job.CollectionName,
            job.SkipPages, job.TotalChunks, job.CompletedChunks, job.BikeId, job.Error,
            job.CreatedAt, job.UpdatedAt, job.CompletedAt);
}
