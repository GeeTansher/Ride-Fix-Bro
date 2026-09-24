namespace RideFixBro.API.Configuration
{
	public sealed class ChatLimitsOptions
	{
		public const string SectionName = "Chat";

		public int MaxMessageCharacters { get; set; } = 2000;

		public int MaxSessionIdCharacters { get; set; } = 128;

		public int MaxImageBytes { get; set; } = 2 * 1024 * 1024;

		public int MaxImagePixels { get; set; } = 4096 * 4096;

		public int MaxRequestBodyBytes { get; set; } = 3 * 1024 * 1024;

		public int RequestsPerMinute { get; set; } = 5;

		public int MaxConcurrentRequests { get; set; } = 1;

		public int MaxHistoryTurns { get; set; } = 10;

		public int MaxToolRounds { get; set; } = 5;

		public int MaxToolCallsPerRequest { get; set; } = 6;

		public void Validate()
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMessageCharacters);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSessionIdCharacters);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImageBytes);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxImagePixels);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestBodyBytes);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RequestsPerMinute);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentRequests);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxHistoryTurns);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxToolRounds);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxToolCallsPerRequest);
		}
	}
}
