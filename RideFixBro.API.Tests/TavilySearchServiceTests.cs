using RideFixBro.API.Services;
using System.Net;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class TavilySearchServiceTests
	{
		[Fact]
		public async Task AReusedHttpClientPreservesSourcesWithAndWithoutASummary()
		{
			const string summaryAndSources = """
				{"answer":"Search summary","results":[{"title":"Rider report","url":"https://reviews.example.test/post","content":"One rider's experience"}]}
				""";
			const string results = """{"answer":null,"results":[{"title":"Source","content":"Useful result"}]}""";
			using var handler = new RecordingHandler(summaryAndSources, results);
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);

			Assert.Equal(summaryAndSources, await service.SearchInternetAsync("first"));
			Assert.Equal(results, await service.SearchInternetAsync("second"));
		}

		[Theory]
		[InlineData("null")]
		[InlineData("[]")]
		[InlineData("{")]
		public async Task InvalidResponseCannotBePresentedAsSearchEvidence(string response)
		{
			using var handler = new RecordingHandler(response);
			using var client = new HttpClient(handler);
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);
			await Assert.ThrowsAnyAsync<JsonException>(() => service.SearchInternetAsync("search"));
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

		[Fact]
		public async Task RequestCancellationStopsTheInternetHttpCall()
		{
			using var handler = new WaitingHandler();
			using var client = new HttpClient(handler);
			var service = new TavilySearchService(ToolFlowTests.Configuration(), client);
			using var cancellation = new CancellationTokenSource();
			var search = service.SearchInternetAsync("search", cancellation.Token);
			await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
		}

		private sealed class WaitingHandler : HttpMessageHandler
		{
			public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

			protected override async Task<HttpResponseMessage> SendAsync(
				HttpRequestMessage request, CancellationToken cancellationToken)
			{
				Started.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				throw new InvalidOperationException("The request should have been cancelled.");
			}
		}
	}
}
