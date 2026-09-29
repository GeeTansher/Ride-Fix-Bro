using AutoGen.Core;

namespace RideFixBro.API.DataStore.Interfaces
{
	public interface IChatHistoryStore
	{
		List<IMessage> GetHistory(string sessionId);
		int? GetSelectedBikeId(string sessionId);
		bool IsGeneralSession(string sessionId);
		Task<T> UpdateHistoryAsync<T>(string sessionId, Func<List<IMessage>, Task<T>> update,
			CancellationToken cancellationToken = default, int? userBikeId = null, bool isGeneral = false);
	}
}
