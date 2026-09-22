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

		public TavilySearchService(IConfiguration config, HttpClient httpClient)
		{
			_apiKey = config["API_Keys:Tavily_Api_key"]
				?? throw new InvalidOperationException("Tavily API key is missing.");
			_httpClient = httpClient;
		}

		// Ye [Function] tag AutoGen ko batata hai ki AI isko use kar sakta hai
		[Function]
		[Description("Internet par live search karne ke liye is tool ka use karein. Ye latest data, prices, aur market info layega.")]
		public async Task<string> SearchInternetAsync([Description("Search query jise internet par dhoondhna hai, jaise 'latest riding jacket price'")] string query)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(query);
			var requestBody = new
			{
				api_key = _apiKey,
				query = query,
				search_depth = "basic",
				include_answer = true,
				max_results = 3
			};

			using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
			using var response = await _httpClient.PostAsync("https://api.tavily.com/search", content);
			response.EnsureSuccessStatusCode();

			var resultStr = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(resultStr);

			if (doc.RootElement.TryGetProperty("answer", out var answerElement) &&
				answerElement.ValueKind == JsonValueKind.String)
			{
				var answer = answerElement.GetString();
				if (!string.IsNullOrWhiteSpace(answer))
				{
					return answer;
				}
			}

			return resultStr;
		}
	}
}