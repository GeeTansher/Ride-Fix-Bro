using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class User
{
    public int Id { get; set; }

    public Guid SupabaseUserId { get; set; }

    public string Email { get; set; } = null!;

    public int RoleId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual MasterUserRole Role { get; set; } = null!;

    public virtual ICollection<UserBike> UserBikes { get; set; } = new List<UserBike>();
}
