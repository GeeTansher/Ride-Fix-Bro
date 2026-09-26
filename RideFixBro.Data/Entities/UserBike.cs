using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class UserBike
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int BikeId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual MasterBike Bike { get; set; } = null!;

    public virtual ICollection<ChatSession> ChatSessionUserBikeNavigations { get; set; } = new List<ChatSession>();

    public virtual ICollection<ChatSession> ChatSessionUserBikes { get; set; } = new List<ChatSession>();

    public virtual User User { get; set; } = null!;
}
