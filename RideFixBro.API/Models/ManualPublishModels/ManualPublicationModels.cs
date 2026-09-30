namespace RideFixBro.API.Models.ManualPublishModels;

public sealed record ManualPublicationSpec(string Make, string Model, int Year, string ManualKey, int SkipPages);
public sealed record ManualPublicationWork(Guid JobId, string CollectionName, string ManualKey,
    IReadOnlyList<string> Chunks, int CompletedChunks);

public sealed record ManualPublicationJobResponse(Guid Id, string Status, string Make, string Model, int Year,
    string ManualKey, string CollectionName, int SkipPages, int TotalChunks, int CompletedChunks,
    int? BikeId, string? Error, DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt);

public static class ManualPublicationStatus
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
