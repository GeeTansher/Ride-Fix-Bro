using RideFixBro.API.Services;
using System.Net;

namespace RideFixBro.API.Tests
{
	public class TavilySearchServiceTests
	{
		[Fact]
		public async Task AReusedHttpClientSupportsRepeatedSearchesAndMissingAnswerFallback()
		{
			const string results = """{"answer":null,"results":[{"title":"Source","content":"Useful result"}]}""";
			using var handler = new RecordingHandler("""{"answer":"First result"}""", results);
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);

			Assert.Equal("First result", await service.SearchInternetAsync("first"));
			Assert.Equal(results, await service.SearchInternetAsync("second"));
		}

		[Fact]
		public async Task HttpFailureIsNotConvertedToSuccessfulSearchContent()
		{
			using var handler = new RecordingHandler("""{"detail":"Rate limited"}""");
			handler.StatusCodes[0] = HttpStatusCode.TooManyRequests;
			using var client = new HttpClient(handler);
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);

			var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchInternetAsync("search"));
			Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
		}

		[Theory]
		[InlineData("")]
		[InlineData(" ")]
		public async Task MissingQueriesFailBeforeSendingARequest(string query)
		{
			using var handler = new RecordingHandler();
			using var client = new HttpClient(handler);
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);

			await Assert.ThrowsAsync<ArgumentException>(() => service.SearchInternetAsync(query));
			Assert.Empty(handler.Requests);
		}
	}
}
