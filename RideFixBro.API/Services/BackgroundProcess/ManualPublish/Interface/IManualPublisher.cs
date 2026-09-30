using RideFixBro.API.Models.ManualPublishModels;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish.Interface;

// Admin publication depends on this operation, not on the search/embedding details.
public interface IManualPublisher
{
    Task PublishAsync(ManualPublicationWork work, Func<int, CancellationToken, Task> saveCheckpoint, CancellationToken token);
}
