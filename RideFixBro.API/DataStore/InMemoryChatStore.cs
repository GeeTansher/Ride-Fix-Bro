using AutoGen.Core;
using RideFixBro.API.DataStore.Interfaces;
using System.Collections.Concurrent;

namespace RideFixBro.API.DataStore
{
	public class InMemoryChatStore : IChatHistoryStore
	{
		// Ye tera temporary RAM wala DB hai
		// Thread-safe dictionary taaki multiple log ek sath chat karein toh crash na ho
		private readonly ConcurrentDictionary<string, List<IMessage>> _store = new();

		public List<IMessage> GetHistory(string sessionId)
		{
			// Agar history hai toh la, nahi toh khali list de
			return _store.TryGetValue(sessionId, out var history) ? history : [];
		}

		public void SaveHistory(string sessionId, List<IMessage> history)
		{
			_store[sessionId] = history;
		}
	}
}
