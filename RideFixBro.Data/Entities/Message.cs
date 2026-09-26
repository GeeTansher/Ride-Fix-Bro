using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class Message
{
    public int Id { get; set; }

    public int ChatSessionId { get; set; }

    public string Role { get; set; } = null!;

    public string Content { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public virtual ChatSession ChatSession { get; set; } = null!;
}
