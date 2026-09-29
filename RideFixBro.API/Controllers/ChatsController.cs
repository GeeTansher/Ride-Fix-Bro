using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using System.Globalization;
using System.Security.Claims;

namespace RideFixBro.API.Controllers;

[ApiController, Authorize, Route("api/chats")]
public sealed class ChatsController(ChatSessionService chats) : ControllerBase
{
    // First Ask uses sessionId=0 + selection; this controller only lists/reopens saved chats.
    // Reopen uses SQL metadata/history, not client state. Paging: 50 chats / 100 visible messages.
    // beforeId = last returned chat ID; beforeSequence = detail.NextBeforeSequence.
    private int UserId => int.Parse(User.FindFirstValue("app_user_id")!);

    [HttpGet]
    public Task<List<ChatSummary>> List([FromQuery] int? beforeId, CancellationToken token) =>
        chats.ListAsync(UserId, beforeId, token);

    [HttpGet("{id:int:min(1)}")]
    public Task<ChatDetail> Get(int id, [FromQuery] int? beforeSequence, CancellationToken token) =>
        chats.DetailAsync(UserId, id, beforeSequence, token);
}
