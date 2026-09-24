namespace RideFixBro.API.Services
{
	public sealed class ChatInputException : ArgumentException
	{
		public int StatusCode { get; }

		public ChatInputException(string message, int statusCode = StatusCodes.Status400BadRequest)
			: base(message)
		{
			StatusCode = statusCode;
		}
	}

	public sealed class ChatLimitExceededException : InvalidOperationException
	{
		public ChatLimitExceededException(string message) : base(message)
		{
		}
	}
}
