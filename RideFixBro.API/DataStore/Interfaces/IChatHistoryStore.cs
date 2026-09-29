using AutoGen.Core;

namespace RideFixBro.API.DataStore.Interfaces;

public sealed record ChatHistorySnapshot(IReadOnlyList<IMessage> Messages, int LastTurnNumber, int LastSequenceNumber);

public interface IChatHistoryStore
{
    Task<ChatHistorySnapshot> LoadRecentAsync(int chatId, int turnsToKeep, CancellationToken token);
    Task AppendTurnAsync(int chatId, ChatHistorySnapshot previous, IReadOnlyList<IMessage> turn, CancellationToken token);
}
