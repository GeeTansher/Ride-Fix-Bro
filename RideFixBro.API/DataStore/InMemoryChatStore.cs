using AutoGen.Core;
using RideFixBro.API.DataStore.Interfaces;
using System.Collections.Concurrent;

namespace RideFixBro.API.DataStore
{
	public class InMemoryChatStore : IChatHistoryStore
	{
		private readonly ConcurrentDictionary<string, SessionHistory> _store = new();

		public List<IMessage> GetHistory(string sessionId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
			return _store.TryGetValue(sessionId, out var session) ? session.Messages.ToList() : [];
		}

		public async Task<T> UpdateHistoryAsync<T>(string sessionId, Func<List<IMessage>, Task<T>> update,
			CancellationToken cancellationToken = default)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
			var session = _store.GetOrAdd(sessionId, _ => new SessionHistory());
			// Ek session mein ek turn chale; doosre session wale bhai ko wait nahi karwana.
			await session.Gate.WaitAsync(cancellationToken);
			try
			{
				// List ki copy pe kaam kar: beech mein error aaye toh saved history ko chhedna nahi.
				// Message objects shared hain, isliye purane tool calls ko mutate nahi karte.
				var history = session.Messages.ToList();
				var result = await update(history);
				cancellationToken.ThrowIfCancellationRequested();
				session.Messages = history.ToArray();
				return result;
			}
			finally
			{
				session.Gate.Release();
			}
		}

		private sealed class SessionHistory
		{
			public SemaphoreSlim Gate { get; } = new(1, 1);
			public volatile IMessage[] Messages = [];
		}
	}
}
