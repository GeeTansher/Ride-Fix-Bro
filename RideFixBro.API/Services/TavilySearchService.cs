using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AutoGen.Core;

namespace RideFixBro.API.Services
{
	// Partial class hona zaroori hai AutoGen Source Generator ke liye
	public partial class TavilySearchService
	{
		private readonly string _apiKey;
		private readonly HttpClient _httpClient;

		public TavilySearchService(string apiKey)
		{
			_apiKey = apiKey;
			_httpClient = new HttpClient();
		}

		// Ye [Function] tag AutoGen ko batata hai ki AI isko use kar sakta hai
		[Function]
		[Description("Internet par live search karne ke liye is tool ka use karein. Ye latest data, prices, aur market info layega.")]
		public async Task<string> SearchInternetAsync([Description("Search query jise internet par dhoondhna hai, jaise 'latest riding jacket price'")] string query)
		{
			try
			{
				// Tavily ko LLM-friendly request bhej rahe hain
				var requestBody = new
				{
					api_key = _apiKey,
					query = query,
					search_depth = "basic",
					include_answer = true, // Tavily direct answer generate karke dega
					max_results = 3
				};

				var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

				// Note: Timeout 10 sec rakha hai taaki API slow na ho
				_httpClient.Timeout = TimeSpan.FromSeconds(10);
				var response = await _httpClient.PostAsync("https://api.tavily.com/search", content);

				if (!response.IsSuccessStatusCode)
				{
					return "Error: Internet search fail ho gaya. Apni basic knowledge se answer de de bhai.";
				}

				var resultStr = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(resultStr);

				// Tavily ek direct 'answer' deta hai LLM ke liye, hum wahi uthayenge
				if (doc.RootElement.TryGetProperty("answer", out var answerElement) &&
					answerElement.ValueKind == JsonValueKind.String)
				{
					string? answer = answerElement.GetString();
					if (!string.IsNullOrEmpty(answer))
						return answer;
				}

				// Agar direct answer nahi mila toh thoda raw JSON de denge
				return resultStr;
			}
			catch(Exception ex)
			{
				Console.WriteLine("Error in Tavily Search Service: " + ex.ToString());
				throw new Exception($"Error Message: {ex.Message} \n Error Inner Exception: {ex.InnerException}");
			}
		}
	}
}