using AutoGen.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.Data.Entities;
using RideFixBro.API.Models.ChatModels;
using RideFixBro.API.Common;

namespace RideFixBro.API.DataStore;

// ChatSessionService checks ownership before this store receives the persisted chat ID.
public sealed class SqlChatHistoryStore(RideFixBroDbContext database) : IChatHistoryStore
{
    public async Task<ChatHistorySnapshot> LoadRecentAsync(int chatId, int turnsToKeep, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chatId);
        ArgumentOutOfRangeException.ThrowIfNegative(turnsToKeep);
        var rows = database.Messages.AsNoTracking().Where(row => row.ChatSessionId == chatId);
        var last = await rows.OrderByDescending(row => row.SequenceNumber)
            .Select(row => new { row.TurnNumber, row.SequenceNumber }).FirstOrDefaultAsync(token);
        if (last is null) return new([], 0, 0);
        if (turnsToKeep == 0) return new([], last.TurnNumber, last.SequenceNumber);
        var saved = await rows.Where(row => row.TurnNumber > last.TurnNumber - turnsToKeep &&
                row.SequenceNumber <= last.SequenceNumber)
            .OrderBy(row => row.SequenceNumber).ToListAsync(token);
        return new(saved.Select(ChatMessageCodec.Decode).ToArray(), last.TurnNumber, last.SequenceNumber);
    }

    public async Task<int> AppendTurnAsync(ChatContext chat, ChatHistorySnapshot previous, IReadOnlyList<IMessage> turn, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chat.Id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chat.UserId);
        if (turn.Count == 0) throw new ArgumentException("A complete non-empty turn is required.", nameof(turn));
        token.ThrowIfCancellationRequested();
        var sequence = previous.LastSequenceNumber;
        var newChat = chat.Id == 0
            ? new ChatSession { UserId = chat.UserId, UserBikeId = chat.Bike?.UserBikeId, IsActive = true, CreatedAt = DateTime.UtcNow }
            : null;
        var additions = turn.Select(ChatMessageCodec.Encode).ToList();
        foreach (var row in additions)
        {
            if (newChat is null) row.ChatSessionId = chat.Id;
            else row.ChatSession = newChat;
            row.TurnNumber = previous.LastTurnNumber + 1;
            row.SequenceNumber = ++sequence;
        }
        database.Messages.AddRange(additions);
        try
        {
            // One transaction saves a new chat AND its first complete turn. Failure leaves no empty chat.
            await database.SaveChangesAsync(token);
            return newChat?.Id ?? chat.Id;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ChatInputException("Another message was saved to this chat. Reload before retrying.", 409);
        }
    }
}
