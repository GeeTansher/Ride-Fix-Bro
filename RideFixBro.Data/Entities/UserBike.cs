using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class UserBike
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int MasterBikeId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();

    public virtual MasterBike MasterBike { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
