using RideFixBro.API.Common;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish;

public sealed class ManualPublicationWorker(IServiceScopeFactory scopes, ManualPublicationQueue queue,
    ILogger<ManualPublicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queue.Wake(); // Recover queued/interrupted SQL jobs once on startup.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await queue.WaitAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var jobs = scope.ServiceProvider.GetRequiredService<ManualPublicationJobsService>();
                        if (!await jobs.ProcessNextAsync(stoppingToken)) break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (ChatInputException ex) when (ex.StatusCode == 409)
                    {
                        // Another app instance owns the publication lock; do not fail the queued job.
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Background manual publication could not access/process the job queue.");
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
                }
                // No idle SQL polling: next submission/status read (or app restart) wakes this worker.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
