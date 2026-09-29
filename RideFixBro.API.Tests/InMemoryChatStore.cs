using AutoGen.Core;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Services;
using RideFixBro.API.Models;

namespace RideFixBro.API.Tests;

// Test-only history store with the same append/version semantics as SQL; never registered by the API.
internal sealed class InMemoryChatStore : IChatHistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<int, List<IMessage[]>> _turns = [];

    public List<IMessage> GetHistory(int chatId)
    {
        lock (_gate)
            return _turns.TryGetValue(chatId, out var turns) ? turns.SelectMany(turn => turn).ToList() : [];
    }

    public Task<ChatHistorySnapshot> LoadRecentAsync(int chatId, int turnsToKeep, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_turns.TryGetValue(chatId, out var turns)) return Task.FromResult(new ChatHistorySnapshot([], 0, 0));
            return Task.FromResult(new ChatHistorySnapshot(turns.TakeLast(turnsToKeep).SelectMany(turn => turn).ToArray(),
                turns.Count, turns.Sum(turn => turn.Length)));
        }
    }

    public Task<int> AppendTurnAsync(ChatContext chat, ChatHistorySnapshot previous, IReadOnlyList<IMessage> turn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var chatId = chat.Id == 0 ? _turns.Keys.DefaultIfEmpty(0).Max() + 1 : chat.Id;
            if (!_turns.TryGetValue(chatId, out var turns)) _turns[chatId] = turns = [];
            if (turns.Count != previous.LastTurnNumber || turns.Sum(row => row.Length) != previous.LastSequenceNumber)
                throw new ChatInputException("Another turn was saved. Reload before retrying.", 409);
            turns.Add(turn.ToArray());
            return Task.FromResult(chatId);
        }
    }
}
