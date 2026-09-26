using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class MasterUserRole
{
    public int Id { get; set; }

    public string RoleName { get; set; } = null!;

    public string Description { get; set; } = null!;

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
