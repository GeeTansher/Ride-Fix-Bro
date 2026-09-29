using System.ComponentModel.DataAnnotations;

namespace RideFixBro.API.Models
{
	public class ChatRequest
	{
		public string SessionId { get; set; } = string.Empty;
		public string Message { get; set; } = string.Empty;
		// Garage entry ID, master catalog ID nahi. Purane clients ke liye optional hai.
		[Range(1, int.MaxValue)]
		public int? UserBikeId { get; set; }
		// General explicitly select hua hai; existing bike chat mein missing ID se ye alag hai.
		public bool IsGeneral { get; set; }

		// Image frontend se Base64 string format mein aayegi
		// '?' lagaya hai kyunki har request mein image hona zaroori nahi hai
		public string? ImageData { get; set; }
	}
}