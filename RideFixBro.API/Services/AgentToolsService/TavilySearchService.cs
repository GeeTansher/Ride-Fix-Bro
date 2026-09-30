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
		[Description("Search the web for current facts, prices, official technical verification, and owner reviews or Reddit discussions. Returns a summary and source titles, URLs, and excerpts for attribution; community reports are not verified specifications.")]
		public Task<string> SearchInternetAsync([Description("Search query jise internet par dhoondhna hai, jaise 'latest riding jacket price'")] string query)
		{
			return SearchInternetAsync(query, CancellationToken.None);
		}

		public async Task<string> SearchInternetAsync(string query, CancellationToken cancellationToken)
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
			using var response = await _httpClient.PostAsync("https://api.tavily.com/search", content, cancellationToken);
			response.EnsureSuccessStatusCode();

			var resultStr = await response.Content.ReadAsStringAsync(cancellationToken);
			using var doc = JsonDocument.Parse(resultStr);

			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new JsonException("Internet search returned an invalid response object.");
			}

			// Sirf answer return karne se results ke URLs/snippets kho jaate hain; attribution ke liye saath rakho.
			return resultStr;
		}
	}
}