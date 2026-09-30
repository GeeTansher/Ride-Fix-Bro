using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Common;
using RideFixBro.API.DataStore;
using RideFixBro.API.Models.ChatModels;
using RideFixBro.API.Models.GarageModels;
using RideFixBro.Data.Entities;

namespace RideFixBro.API.Services;

public sealed class ChatSessionService(RideFixBroDbContext database)
{
    public async Task<ChatContext> PrepareNewAsync(int userId, int? userBikeId, bool isGeneral, CancellationToken token)
    {
        if (isGeneral == userBikeId.HasValue)
            throw new ChatInputException("Select either General or one garage bike.");
        UserBike? bike = null;
        if (userBikeId.HasValue)
            bike = await database.UserBikes.AsNoTracking().Include(row => row.Bike)
                .SingleOrDefaultAsync(row => row.Id == userBikeId && row.UserId == userId && !row.IsDeleted, token)
                ?? throw new ChatInputException("Selected bike is not in your active garage.", 404);
        // No chat row yet. SqlChatHistoryStore saves it only with the first complete successful turn.
        return new ChatContext(0, ToBikeContext(bike), userId);
    }

    public async Task<ChatContext> GetContextAsync(int userId, int id, CancellationToken token)
    {
        var chat = await GetAsync(userId, id, token);
        return new ChatContext(chat.Id, ToBikeContext(chat.UserBike), chat.UserId);
    }

    private async Task<ChatSession> GetAsync(int userId, int id, CancellationToken token) =>
        await database.ChatSessions.AsNoTracking().Include(chat => chat.UserBike).ThenInclude(bike => bike!.Bike)
            .SingleOrDefaultAsync(chat => chat.Id == id && chat.UserId == userId && chat.IsActive, token)
        ?? throw new ChatInputException("Chat not found.", 404);

    public async Task<List<ChatSummary>> ListAsync(int userId, int? beforeId, DateTime? beforeUpdatedAt, CancellationToken token)
    {
        if (beforeId.HasValue != beforeUpdatedAt.HasValue || beforeId is <= 0)
            throw new ChatInputException("Provide both beforeId and beforeUpdatedAt from the last listed chat.");
        var query = database.ChatSessions.AsNoTracking().Include(chat => chat.UserBike).ThenInclude(bike => bike!.Bike)
            .Where(chat => chat.UserId == userId && chat.IsActive)
            .Select(chat => new { Chat = chat,
                Title = chat.Messages.Where(message => message.Role == "User").OrderBy(message => message.SequenceNumber)
                    .Select(message => message.Content).FirstOrDefault(),
                Updated = chat.Messages.Select(message => (DateTime?)message.Timestamp).Max() ?? chat.CreatedAt });
        if (beforeId.HasValue)
        {
            // Cursor values come from the previous page, not from a row that can change or be deleted.
            query = query.Where(row => row.Updated < beforeUpdatedAt ||
                (row.Updated == beforeUpdatedAt && row.Chat.Id < beforeId));
        }
        var rows = await query.OrderByDescending(row => row.Updated).ThenByDescending(row => row.Chat.Id).Take(50).ToListAsync(token);
        return rows.Select(row => Summary(row.Chat, row.Title ?? "New chat", row.Updated)).ToList();
    }

    public async Task<ChatDetail> DetailAsync(int userId, int id, int? beforeSequence, CancellationToken token)
    {
        var chat = await GetAsync(userId, id, token);
        var rows = await database.Messages.AsNoTracking()
            .Where(row => row.ChatSessionId == id && (row.Role == "User" || row.Role == "Assistant") &&
                (!beforeSequence.HasValue || row.SequenceNumber < beforeSequence))
            .OrderByDescending(row => row.SequenceNumber).Take(101).ToListAsync(token);
        var more = rows.Count > 100;
        var page = rows.Take(100).Reverse().ToList();
        var messages = page.Select(row => new SavedChatMessage(row.SequenceNumber, row.Content ?? "",
            row.Role == "User", ChatMessageCodec.PhotoNotStored(row))).ToList();
        var metadata = await database.ChatSessions.Where(row => row.Id == id).Select(row => new
        {
            Title = row.Messages.Where(message => message.Role == "User").OrderBy(message => message.SequenceNumber)
                .Select(message => message.Content).FirstOrDefault(),
            Updated = row.Messages.Select(message => (DateTime?)message.Timestamp).Max() ?? row.CreatedAt
        }).SingleAsync(token);
        return new(Summary(chat, metadata.Title ?? "New chat", metadata.Updated),
            messages, more ? page[0].SequenceNumber : null);
    }

    public async Task DeleteAsync(int userId, int id, CancellationToken token)
    {
        // The existing database FK cascades to Messages; garage bikes and other chats are untouched.
        if (await database.ChatSessions.Where(chat => chat.Id == id && chat.UserId == userId).ExecuteDeleteAsync(token) == 0)
            throw new ChatInputException("Chat not found.", 404);
    }

    private static BikeContext? ToBikeContext(UserBike? bike) => bike is null ? null :
        new(bike.Id, bike.Bike.Make, bike.Bike.Model, bike.Bike.Year, bike.Bike.ManualKey);

    private static GarageBikeResponse? ToBikeResponse(UserBike? bike) => bike is null ? null :
        new(bike.Id, bike.BikeId, bike.Bike.Make, bike.Bike.Model, bike.Bike.Year, bike.CreatedAt);

    private static ChatSummary Summary(ChatSession chat, string title, DateTime updated) =>
        new(chat.Id, title.Length > 60 ? title[..60] : title, ToBikeResponse(chat.UserBike), chat.UserBikeId is null, updated);
}
