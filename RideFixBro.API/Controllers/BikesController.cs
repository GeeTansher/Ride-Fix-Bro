using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Services;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/bikes")]
	public class BikesController(GarageService garage) : ControllerBase
	{
		private readonly GarageService _garage = garage;

        [HttpGet]
		public async Task<IActionResult> Get(CancellationToken cancellationToken)
		{
			return Ok(await _garage.GetCatalogAsync(cancellationToken));
		}
	}
}
