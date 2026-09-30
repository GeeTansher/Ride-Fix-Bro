using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Models.GarageModels;
using RideFixBro.API.Services;
using System.Globalization;
using System.Security.Claims;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/garage")]
	public class GarageController : ControllerBase
	{
		private readonly GarageService _garage;

		public GarageController(GarageService garage)
		{
			_garage = garage;
		}

		[HttpGet]
		public async Task<IActionResult> Get(CancellationToken cancellationToken)
		{
			var userId = int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);
			return Ok(await _garage.GetGarageAsync(userId, cancellationToken));
		}

		[HttpPost]
		public async Task<IActionResult> Add([FromBody] AddGarageBikeRequest request, CancellationToken cancellationToken)
		{
			// UserId token/SQL mapping se aayega; body mein sirf catalog BikeId chahiye.
			var userId = int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);
			var result = await _garage.AddAsync(userId, request.BikeId, cancellationToken);
			if (result.Bike is null)
			{
				return NotFound(new { Error = "Bhai, ye bike catalog mein nahi hai. List se bike select kar." });
			}
			if (!result.Created)
			{
				return Ok(result.Bike);
			}
			return CreatedAtAction(nameof(Get), result.Bike);
		}

		[HttpDelete("{id:int:min(1)}")]
		public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
		{
			var userId = int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);
			if (!await _garage.DeleteAsync(userId, id, cancellationToken))
			{
				return NotFound(new { Error = "Bhai, ye bike teri garage mein nahi mili." });
			}
			return NoContent();
		}
	}
}
