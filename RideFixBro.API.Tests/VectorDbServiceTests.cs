using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RideFixBro.API.Services;
using System.ClientModel;
using System.Net;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class VectorDbServiceTests
	{
		[Fact]
		public async Task SearchReturnsManualPayloadUsingTheGeneratedWrapper()
		{
			using var embeddingHandler = EmbeddingResponse();
			var invoker = new QueryInvoker(
				new ScoredPoint { Payload = { ["text"] = "Manual passage A" } },
				new ScoredPoint { Payload = { ["text"] = "Manual passage B" } });
			using var client = new QdrantClient(new QdrantGrpcClient(invoker));
			var service = new VectorDbService(client,
				ToolFlowTests.CreateOpenAIClient(embeddingHandler).GetEmbeddingClient("gemini-embedding-2-preview"));

			var result = await service.SearchManualAsyncWrapper("""{"userQuery":"manual instructions"}""");

			Assert.Contains("Manual passage A", result);
			Assert.Contains("Manual passage B", result);
			var request = Assert.Single(invoker.Requests);
			Assert.Equal("X440_Manual", request.CollectionName);
			Assert.Equal(3UL, request.Limit);
			Assert.True(request.WithPayload.Enable);
			Assert.Equal(3072, request.Query.Nearest.Dense.Data.Count);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task SuccessfulSearchWithoutUsablePayloadReturnsAnExplicitNoMatch(bool emptyPayload)
		{
			using var embeddingHandler = EmbeddingResponse();
			var invoker = emptyPayload
				? new QueryInvoker(new ScoredPoint { Payload = { ["text"] = " " } })
				: new QueryInvoker();
			using var client = new QdrantClient(new QdrantGrpcClient(invoker));
			var service = new VectorDbService(client,
				ToolFlowTests.CreateOpenAIClient(embeddingHandler).GetEmbeddingClient("gemini-embedding-2-preview"));

			Assert.Contains("No relevant manual passages", await service.SearchManualAsync("manual instructions"));
		}

		[Fact]
		public async Task QdrantFailureIsNotSwallowedAsAnEmptyResult()
		{
			using var embeddingHandler = EmbeddingResponse();
			var invoker = new QueryInvoker { Error = new RpcException(new Status(StatusCode.Unavailable, "Test outage")) };
			using var client = new QdrantClient(new QdrantGrpcClient(invoker));
			var service = new VectorDbService(client,
				ToolFlowTests.CreateOpenAIClient(embeddingHandler).GetEmbeddingClient("gemini-embedding-2-preview"));

			var error = await Assert.ThrowsAsync<RpcException>(() => service.SearchManualAsync("manual instructions"));
			Assert.Same(invoker.Error, error);
		}

		[Fact]
		public async Task EmbeddingFailureIsPreservedAndQdrantIsNotQueried()
		{
			using var embeddingHandler = new RecordingHandler(
				"""{"error":{"message":"Embedding unavailable","type":"invalid_request_error","code":"invalid_argument"}}""");
			embeddingHandler.StatusCodes[0] = HttpStatusCode.BadRequest;
			var invoker = new QueryInvoker();
			using var client = new QdrantClient(new QdrantGrpcClient(invoker));
			var service = new VectorDbService(client,
				ToolFlowTests.CreateOpenAIClient(embeddingHandler).GetEmbeddingClient("gemini-embedding-2-preview"));

			var error = await Assert.ThrowsAsync<ClientResultException>(() => service.SearchManualAsync("manual instructions"));
			Assert.Equal(400, error.Status);
			Assert.Empty(invoker.Requests);
		}

		private static RecordingHandler EmbeddingResponse() => new(JsonSerializer.Serialize(new
		{
			@object = "list",
			data = new[]
			{
				new { @object = "embedding", index = 0, embedding = Convert.ToBase64String(new byte[3072 * sizeof(float)]) }
			},
			model = "gemini-embedding-2-preview",
			usage = new { prompt_tokens = 1, total_tokens = 1 }
		}));

		private sealed class QueryInvoker(params ScoredPoint[] results) : CallInvoker
		{
			public List<QueryPoints> Requests { get; } = [];
			public RpcException? Error { get; init; }

			public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
				Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
			{
				Requests.Add(Assert.IsType<QueryPoints>(request));
				var response = new QueryResponse();
				response.Result.AddRange(results);
				var typedResponse = Assert.IsType<TResponse>(response);
				return new AsyncUnaryCall<TResponse>(
					Error is null ? Task.FromResult(typedResponse) : Task.FromException<TResponse>(Error),
					Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
			}

			public override TResponse BlockingUnaryCall<TRequest, TResponse>(
				Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
				throw new NotSupportedException();

			public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
				Method<TRequest, TResponse> method, string? host, CallOptions options) =>
				throw new NotSupportedException();

			public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
				Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
				throw new NotSupportedException();

			public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
				Method<TRequest, TResponse> method, string? host, CallOptions options) =>
				throw new NotSupportedException();
		}
	}
}
