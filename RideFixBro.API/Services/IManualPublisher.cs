namespace RideFixBro.API.Services;

// Admin publication depends on this operation, not on the search/embedding details.
public interface IManualPublisher
{
    Task<ManualUploadResult> ReplaceManualAsync(byte[] pdf, string manualKey, int skipPages, CancellationToken token);
}
