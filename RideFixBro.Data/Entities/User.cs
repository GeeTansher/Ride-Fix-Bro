using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class User
{
    public int Id { get; set; }

    public string Email { get; set; } = null!;

    public string Role { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();

    public virtual ICollection<UserBike> UserBikes { get; set; } = new List<UserBike>();
}
