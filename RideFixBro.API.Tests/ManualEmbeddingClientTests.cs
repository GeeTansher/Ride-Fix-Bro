using OpenAI.Embeddings;
using RideFixBro.API.Common;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;
using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RideFixBro.API.Tests;

public class ManualEmbeddingClientTests
{
    [Fact]
    public async Task ReportedTokensPauseTheNextChunkEvenBelowTheRequestLimit()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Success(24_000), Success(100));
        var client = Create(handler, clock);

        await client.GenerateAsync("First chunk", CancellationToken.None);
        await client.GenerateAsync("Next chunk", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(61), Assert.Single(clock.Delays));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("gemini-embedding-2-preview", request.GetProperty("model").GetString());
            Assert.Single(request.GetProperty("input").EnumerateArray());
        });
        Assert.Equal("Next chunk", handler.Requests[1].GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task ReservesSpaceForTheNextChunksTextBeforeCallingGoogle()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Success(23_999), Success(10));
        var client = Create(handler, clock);
        await client.GenerateAsync("first", CancellationToken.None);
        await client.GenerateAsync("next", CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(61), Assert.Single(clock.Delays));
    }

    [Fact]
    public async Task RequestBudgetAndRollingExpiryAreRespectedAcrossCalls()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Success(1), Success(1), Success(1));
        var client = Create(handler, clock, requestsPerMinute: 2);
        await client.GenerateAsync("one", CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(30);
        await client.GenerateAsync("two", CancellationToken.None);
        await client.GenerateAsync("three", CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(31), Assert.Single(clock.Delays));
    }

    [Fact]
    public async Task UsesMeasuredUsageRatherThanRetainingTheConservativeReservation()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Success(10), Success(10));
        var client = Create(handler, clock);
        await client.GenerateAsync(new string('a', 20_000), CancellationToken.None);
        await client.GenerateAsync(new string('b', 20_000), CancellationToken.None);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task MissingUsageUsesConservativeTextAccountingInsteadOfZero()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Success(0), Success(0));
        var client = Create(handler, clock);
        await client.GenerateAsync(new string('a', 24_000), CancellationToken.None);
        await client.GenerateAsync("next", CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(61), Assert.Single(clock.Delays));
    }

    [Theory]
    [InlineData(null, 61)]
    [InlineData("12", 12)]
    [InlineData("Wed, 30 Sep 2026 00:00:30 GMT", 30)]
    [InlineData("0", 1)]
    [InlineData("invalid", 61)]
    public async Task ThrottledChunkHonorsRetryAfterThenReturnsItsEmbedding(string? retryAfter, int waitSeconds)
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Throttled(retryAfter), Success(15));
        var result = await Create(handler, clock).GenerateAsync("same chunk", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(waitSeconds), Assert.Single(clock.Delays));
        Assert.Equal(3072, result.Length);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].GetRawText(), handler.Requests[1].GetRawText());
    }

    [Fact]
    public async Task PersistentQuotaFailureStopsAfterTwoRetriesWithExplicitProviderError()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Throttled(), Throttled(), Throttled());
        var error = await Assert.ThrowsAsync<ChatInputException>(() => Create(handler, clock)
            .GenerateAsync("chunk", CancellationToken.None));

        Assert.Equal(429, error.StatusCode);
        Assert.Contains("Gemini embedding quota", error.Message);
        Assert.Contains("daily quota", error.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, clock.Delays.Count);
        Assert.All(clock.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(61), delay));
    }

    [Fact]
    public async Task ProviderCooldownLongerThanTwoMinutesStopsWithoutRetryingEarly()
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(Throttled("3600"));
        var client = Create(handler, clock);
        await Assert.ThrowsAsync<ChatInputException>(() => client.GenerateAsync("chunk", CancellationToken.None));
        await Assert.ThrowsAsync<ChatInputException>(() => client.GenerateAsync("next upload", CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NonQuotaFailuresAreNotRetriedOrDisguised(HttpStatusCode status)
    {
        var clock = new TestClock();
        using var handler = new EmbeddingHandler(new Response(status, """{"error":{"message":"Original failure"}}"""));
        var error = await Assert.ThrowsAsync<ClientResultException>(() => Create(handler, clock)
            .GenerateAsync("chunk", CancellationToken.None));
        Assert.Equal((int)status, error.Status);
        Assert.Single(handler.Requests);
        Assert.Empty(clock.Delays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationInterruptsBudgetAndProviderWaits(bool quotaWait)
    {
        var clock = new TestClock();
        using var cancellation = new CancellationTokenSource();
        using var handler = new EmbeddingHandler(quotaWait ? Throttled() : Success(24_000));
        var client = Create(handler, clock);
        if (!quotaWait) await client.GenerateAsync("first", cancellation.Token);
        clock.BeforeDelay = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync("next", cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(0, 24000)]
    [InlineData(80, 0)]
    [InlineData(-1, 24000)]
    public void InvalidBudgetCannotDisablePacing(int rpm, int tpm)
    {
        using var handler = new EmbeddingHandler();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(handler, new TestClock(), rpm, tpm));
    }

    private static ManualEmbeddingClient Create(EmbeddingHandler handler, TestClock clock,
        int requestsPerMinute = 80, int tokensPerMinute = 24_000)
    {
        var client = new OpenAI.OpenAIClient(new ApiKeyCredential("test"), new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://embedding.test/v1beta/openai/"),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0)
        }).GetEmbeddingClient("gemini-embedding-2-preview");
        return new ManualEmbeddingClient(client, requestsPerMinute, tokensPerMinute, () => clock.Now, clock.Delay);
    }

    private static Response Success(int inputTokens) => new(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        @object = "list",
        data = new[] { new { @object = "embedding", index = 0, embedding = Convert.ToBase64String(new byte[3072 * sizeof(float)]) } },
        model = "gemini-embedding-2-preview",
        usage = new { prompt_tokens = inputTokens, total_tokens = inputTokens }
    }));

    private static Response Throttled(string? retryAfter = null) => new(HttpStatusCode.TooManyRequests,
        """{"error":{"message":"Embedding quota exceeded","type":"rate_limit_error","code":429}}""", retryAfter);

    private sealed record Response(HttpStatusCode Status, string Body, string? RetryAfter = null);

    private sealed class EmbeddingHandler(params Response[] responses) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(body.RootElement.Clone());
            Assert.True(Requests.Count <= responses.Length, "Unexpected embedding request.");
            var reply = responses[Requests.Count - 1];
            var response = new HttpResponseMessage(reply.Status)
            {
                Content = new StringContent(reply.Body, Encoding.UTF8, "application/json")
            };
            if (reply.RetryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", reply.RetryAfter);
            return response;
        }
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public Action? BeforeDelay { get; set; }
        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            BeforeDelay?.Invoke();
            token.ThrowIfCancellationRequested();
            Delays.Add(duration);
            Now += duration;
            return Task.CompletedTask;
        }
    }
}
