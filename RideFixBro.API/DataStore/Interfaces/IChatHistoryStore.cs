using AutoGen.Core;
using RideFixBro.API.Models.ChatModels;

namespace RideFixBro.API.DataStore.Interfaces;

public sealed record ChatHistorySnapshot(IReadOnlyList<IMessage> Messages, int LastTurnNumber, int LastSequenceNumber);

public interface IChatHistoryStore
{
    Task<ChatHistorySnapshot> LoadRecentAsync(int chatId, int turnsToKeep, CancellationToken token);
    Task<int> AppendTurnAsync(ChatContext chat, ChatHistorySnapshot previous, IReadOnlyList<IMessage> turn, CancellationToken token);
}
