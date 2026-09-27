using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Authorize]
	[Route("api/me")]
	public class MeController : ControllerBase
	{
		[HttpGet]
		public IActionResult Get()
		{
			return Ok(new
			{
				Id = int.Parse(User.FindFirstValue("app_user_id")!, System.Globalization.CultureInfo.InvariantCulture),
				SupabaseUserId = User.FindFirstValue("sub"),
				Email = User.FindFirstValue("email"),
				Role = User.FindFirstValue(ClaimTypes.Role)
			});
		}
	}
}
