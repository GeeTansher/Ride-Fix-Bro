using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using RideFixBro.API.Configuration;
using RideFixBro.API.Services;
using System.Security.Claims;
using System.Globalization;
using RideFixBro.API.Common;
using RideFixBro.API.Models.ChatModels;

namespace RideFixBro.API.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class ChatController(AiManagerService aiManager, SemaphoreSlim chatSlots,
		ChatSessionService chats, ChatInputValidator inputValidator, ILogger<ChatController> logger) : ControllerBase
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

			int? assignedSessionId = null;
			try
			{
				// AiManagerService ko message pass kiya
				var appUserId = int.Parse(User.FindFirstValue("app_user_id")!, CultureInfo.InvariantCulture);
				if (request.IsGeneral && request.UserBikeId.HasValue)
					throw new ChatInputException("Select either General or one garage bike.");
				// Validate/decode once, before new chat creation; reuse the owned context for the entire turn.
				var input = inputValidator.Validate(request.Message, request.ImageData);
				var chat = request.SessionId == 0
					? await chats.PrepareNewAsync(appUserId, request.UserBikeId, request.IsGeneral, cancellationToken)
					: await chats.GetContextAsync(appUserId, request.SessionId, cancellationToken);
				assignedSessionId = chat.Id == 0 ? null : chat.Id;
				if ((request.UserBikeId.HasValue && request.UserBikeId != chat.Bike?.UserBikeId) ||
					(request.IsGeneral && !chat.IsGeneral))
					throw new ChatInputException("Chat selection locked hai. New Chat kholo.", 409);
				var response = await _aiManager.AskMechanicBro(chat, input, cancellationToken);
				return Ok(response);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Request cancel hui hai; ise neeche wale catch mein server crash mat bana.
				throw;
			}
			catch (ChatInputException ex)
			{
				return StatusCode(ex.StatusCode, new { Error = ex.Message, SessionId = assignedSessionId });
			}
			catch (ChatLimitExceededException ex)
			{
				return UnprocessableEntity(new { Error = ex.Message, SessionId = assignedSessionId });
			}
			catch (Exception ex) when (ex is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException
				or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
			{
				cancellationToken.ThrowIfCancellationRequested();
				logger.LogError(ex, "Chat database operation failed.");
				return StatusCode(503, new { Error = "Bhai, database abhi available nahi hai. Thodi der baad retry kar.", SessionId = assignedSessionId });
			}
			catch (Exception ex)
			{
				cancellationToken.ThrowIfCancellationRequested();
				logger.LogError(ex, "Chat request failed.");
				// Asli exception service logs mein hai; provider/internal details client ko mat bhej.
				return StatusCode(500, new { Error = "Bhai, abhi answer nahi aa paaya. Thodi der baad dobara try kar.", SessionId = assignedSessionId });
			}
			finally
			{
				_chatSlots.Release();
			}
		}
	}
}