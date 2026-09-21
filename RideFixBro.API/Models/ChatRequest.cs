namespace RideFixBro.API.Models
{
	public class ChatRequest
	{
		public string SessionId { get; set; } = string.Empty;
		public string Message { get; set; } = string.Empty;

		// Image frontend se Base64 string format mein aayegi
		// '?' lagaya hai kyunki har request mein image hona zaroori nahi hai
		public string? ImageData { get; set; }
	}
}