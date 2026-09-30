using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RideFixBro.API.Models.ChatModels
{
	public class ChatRequest
	{
		// 0 = first message/new chat; positive ID = existing owned chat. JSON number, string nahi.
		[Range(0, int.MaxValue)]
		[JsonNumberHandling(JsonNumberHandling.Strict)]
		public int SessionId { get; set; }
		public string Message { get; set; } = string.Empty;
		// New chat ki selection; existing chat mein sirf conflict check, actual selection SQL se.
		[Range(1, int.MaxValue)]
		public int? UserBikeId { get; set; }
		// General explicitly select hua hai; existing bike chat mein missing ID se ye alag hai.
		public bool IsGeneral { get; set; }

		// Image frontend se Base64 string format mein aayegi
		// '?' lagaya hai kyunki har request mein image hona zaroori nahi hai
		public string? ImageData { get; set; }
	}
}