using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class Message
{
    public int Id { get; set; }

    public int ChatSessionId { get; set; }

    public string Role { get; set; } = null!;

    public string? Content { get; set; }

    public string? PayloadJson { get; set; }

    public int TurnNumber { get; set; }

    public int SequenceNumber { get; set; }

    public DateTime Timestamp { get; set; }

    public virtual ChatSession ChatSession { get; set; } = null!;
}
