using System.ComponentModel.DataAnnotations;

namespace RideFixBro.API.Models.GarageModels
{
	public class AddGarageBikeRequest
	{
		[Range(1, int.MaxValue)]
		public int BikeId { get; set; }
	}

	public record BikeResponse(int Id, string Make, string Model, int Year);

	public record GarageBikeResponse(int Id, int BikeId, string Make, string Model, int Year, DateTime CreatedAt);
}
