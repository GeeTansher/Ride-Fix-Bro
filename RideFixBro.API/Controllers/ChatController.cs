using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using RideFixBro.API.Configuration;
using RideFixBro.API.Filters;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using System.Security.Claims;
using RideFixBro.API.DataStore.Interfaces;
using System.Globalization;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class ChatController(AiManagerService aiManager, SemaphoreSlim chatSlots,
		GarageService garage, IChatHistoryStore history, ILogger<ChatController> logger) : ControllerBase
	{
		private readonly AiManagerService _aiManager = aiManager;
		private readonly SemaphoreSlim _chatSlots = chatSlots;

        [HttpPost("ask")]
		// Ye do rules sirf Ask ke liye hain, poore controller ke liye nahi.
		[EnableRateLimiting(ChatLimitsOptions.SectionName)]
		[ServiceFilter(typeof(ChatBodyLimit))]
		[Authorize]
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
				var userId = Guid.Parse(User.FindFirstValue("sub")!);
				var appUserId = int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);
				if (request.IsGeneral && request.UserBikeId.HasValue)
				{
					return BadRequest(new { Error = "General aur bike dono select nahi ho sakte." });
				}
				var historyKey = $"{userId:D}:{request.SessionId}";
				var lockedBike = history.GetSelectedBikeId(historyKey);
				var lockedGeneral = history.IsGeneralSession(historyKey);
				var isGeneral = request.IsGeneral || lockedGeneral;
				if ((isGeneral && lockedBike.HasValue) || (lockedGeneral && request.UserBikeId.HasValue))
				{
					return Conflict(new { Error = "Chat selection locked hai. Change karne ke liye New Chat kholo." });
				}
				if (lockedBike.HasValue && request.UserBikeId.HasValue && lockedBike != request.UserBikeId)
				{
					return Conflict(new { Error = "Bhai, bike locked hai. New Chat mein doosri bike select kar." });
				}
				var selectedBikeId = request.UserBikeId ?? lockedBike;
				GarageBikeResponse? selectedBike = null;
				if (selectedBikeId.HasValue)
				{
					selectedBike = await garage.GetForChatAsync(appUserId, selectedBikeId.Value,
						includeDeleted: lockedBike == selectedBikeId, cancellationToken);
					if (selectedBike is null)
					{
						return NotFound(new { Error = "Bhai, selected bike teri active garage mein nahi hai." });
					}
				}
				var response = await _aiManager.AskMechanicBro(
					userId, request.SessionId, request.Message, request.ImageData, cancellationToken, selectedBike, isGeneral);
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
			catch (Exception ex)
			{
				cancellationToken.ThrowIfCancellationRequested();
				logger.LogError(ex, "Chat request failed.");
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