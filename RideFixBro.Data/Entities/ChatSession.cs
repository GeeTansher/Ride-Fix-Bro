using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class ChatSession
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int UserBikeId { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Message> Messages { get; set; } = new List<Message>();

    public virtual UserBike UserBike { get; set; } = null!;
}
