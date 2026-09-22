using AutoGen.Core;

namespace RideFixBro.API.DataStore.Interfaces
{
	public interface IChatHistoryStore
	{
		List<IMessage> GetHistory(string sessionId);
		Task<T> UpdateHistoryAsync<T>(string sessionId, Func<List<IMessage>, Task<T>> update,
			CancellationToken cancellationToken = default);
	}
}
