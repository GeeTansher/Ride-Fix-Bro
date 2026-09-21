using AutoGen.Core;

namespace RideFixBro.API.DataStore.Interfaces
{
	public interface IChatHistoryStore
	{
		List<IMessage> GetHistory(string sessionId);
		void SaveHistory(string sessionId, List<IMessage> history);
	}
}
