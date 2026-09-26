using System;
using System.Collections.Generic;

namespace RideFixBro.Data.Entities;

public partial class MasterBike
{
    public int Id { get; set; }

    public string Make { get; set; } = null!;

    public string Model { get; set; } = null!;

    public int Year { get; set; }

    public virtual ICollection<UserBike> UserBikes { get; set; } = new List<UserBike>();
}
