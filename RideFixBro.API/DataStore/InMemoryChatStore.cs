using AutoGen.Core;
using RideFixBro.API.DataStore.Interfaces;
using System.Collections.Concurrent;
using RideFixBro.API.Services;

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

		public int? GetSelectedBikeId(string sessionId)
		{
			return _store.TryGetValue(sessionId, out var session) && session.BikeId > 0 ? session.BikeId : null;
		}

		public bool IsGeneralSession(string sessionId)
		{
			return _store.TryGetValue(sessionId, out var session) && session.IsGeneral;
		}

		public async Task<T> UpdateHistoryAsync<T>(string sessionId, Func<List<IMessage>, Task<T>> update,
			CancellationToken cancellationToken = default, int? userBikeId = null, bool isGeneral = false)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
			var session = _store.GetOrAdd(sessionId, _ => new SessionHistory());
			// Ek session mein ek turn chale; doosre session wale bhai ko wait nahi karwana.
			await session.Gate.WaitAsync(cancellationToken);
			try
			{
				var requestedBike = userBikeId ?? 0;
				if (session.BikeId != -1 &&
					(session.BikeId != requestedBike || session.IsGeneral != isGeneral))
				{
					throw new ChatInputException("Bhai, is chat ki selection locked hai. Change karne ke liye New Chat kholo.", 409);
				}
				// First accepted attempt General/bike selection lock karta hai, chahe model reply fail ho.
				session.IsGeneral = isGeneral;
				session.BikeId = requestedBike;
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
			public volatile int BikeId = -1;
			public volatile bool IsGeneral;
		}
	}
}
