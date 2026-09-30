using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class ManualPublicationJob
{
    public Guid Id { get; set; }

    public int SubmittedByUserId { get; set; }

    public string Make { get; set; } = null!;

    public string Model { get; set; } = null!;

    public int Year { get; set; }

    public string ManualKey { get; set; } = null!;

    public string CollectionName { get; set; } = null!;

    public int SkipPages { get; set; }

    public string? ChunksJson { get; set; }

    public int TotalChunks { get; set; }

    public int CompletedChunks { get; set; }

    public string Status { get; set; } = null!;

    public string? Error { get; set; }

    public int? BikeId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual MasterBike? Bike { get; set; }

    public virtual User SubmittedByUser { get; set; } = null!;
}
