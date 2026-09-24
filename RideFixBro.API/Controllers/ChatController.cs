using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RideFixBro.API.Configuration;
using RideFixBro.API.Filters;
using RideFixBro.API.Models;
using RideFixBro.API.Services;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class ChatController(AiManagerService aiManager, SemaphoreSlim chatSlots) : ControllerBase
	{
		private readonly AiManagerService _aiManager = aiManager;
		private readonly SemaphoreSlim _chatSlots = chatSlots;

        [HttpPost("ask")]
		// Ye do rules sirf Ask ke liye hain, poore controller ke liye nahi.
		[EnableRateLimiting(ChatLimitsOptions.SectionName)]
		[ServiceFilter(typeof(ChatBodyLimit))]
		// Bhai, ye token JSON se nahi aata; ASP.NET request abort hone ka signal deta hai.
		public async Task<IActionResult> AskBro([FromBody] ChatRequest request, CancellationToken cancellationToken)
		{
			// Ask calls ye slots share karti hain. Busy ho toh wait nahi, seedha 429.
			if (!await _chatSlots.WaitAsync(0, cancellationToken))
			{
				return StatusCode(429, new { Error = "Bhai, ek chat abhi chal rahi hai. Uske baad dobara try kar." });
			}

			try
			{
				// AiManagerService ko message pass kiya
				var response = await _aiManager.AskMechanicBro(request.SessionId, request.Message, request.ImageData, cancellationToken);
				return Ok(new { Reply = response });
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Request cancel hui hai; ise neeche wale catch mein server crash mat bana.
				throw;
			}
			catch (ChatInputException ex)
			{
				return StatusCode(ex.StatusCode, new { Error = ex.Message });
			}
			catch (ChatLimitExceededException ex)
			{
				return UnprocessableEntity(new { Error = ex.Message });
			}
			catch (Exception)
			{
				// Asli exception service logs mein hai; provider/internal details client ko mat bhej.
				return StatusCode(500, new { Error = "Bhai, abhi answer nahi aa paaya. Thodi der baad dobara try kar." });
			}
			finally
			{
				_chatSlots.Release();
			}
		}
	}
}