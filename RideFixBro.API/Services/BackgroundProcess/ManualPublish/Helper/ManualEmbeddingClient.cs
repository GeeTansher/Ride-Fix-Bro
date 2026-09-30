using OpenAI.Embeddings;
using RideFixBro.API.Common;
using System.ClientModel;
using System.Globalization;
using System.Text;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;

// Used only by the serialized PDF upload loop, not by chat/query embeddings.
// Local accounting leaves headroom; Google still enforces project-wide usage across all app instances.
internal sealed class ManualEmbeddingClient
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(61);
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromMinutes(2);
    private const int MaxRateLimitRetries = 2;
    private readonly EmbeddingClient _client;
    private readonly int _requestsPerMinute;
    private readonly int _tokensPerMinute;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Queue<(DateTimeOffset CompletedAt, int Tokens)> _usage = [];
    private DateTimeOffset _retryNotBefore;

    // Clock/wait parameters let tests verify minute-long pacing without sleeping or calling Google.
    public ManualEmbeddingClient(EmbeddingClient client, int requestsPerMinute, int tokensPerMinute,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokensPerMinute);
        _client = client;
        _requestsPerMinute = requestsPerMinute;
        _tokensPerMinute = tokensPerMinute;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public async Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        // Conservative reservation before the call; this is not an exact Gemini tokenizer.
        // After success, use provider-reported tokens. Keep the estimate if usage is absent.
        var estimatedTokens = Math.Max(1, Encoding.UTF8.GetByteCount(text));
        for (var attempt = 0; ; attempt++)
        {
            await WaitForBudgetAsync(Math.Min(estimatedTokens, _tokensPerMinute), token);
            try
            {
                // One input only: do not merge PDF chunks or change the existing embedding model/space.
                // Collection response retains usage, unlike the single-embedding convenience method.
                var response = await _client.GenerateEmbeddingsAsync(new[] { text }, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                var usedTokens = response.Value.Usage?.InputTokenCount ?? 0;
                _usage.Enqueue((_utcNow(), usedTokens > 0 ? usedTokens : estimatedTokens));
                if (response.Value.Count != 1)
                    throw new InvalidOperationException("Expected exactly one PDF chunk embedding.");
                return response.Value[0].ToFloats();
            }
            catch (ClientResultException error) when (error.Status == 429)
            {
                token.ThrowIfCancellationRequested();
                var now = _utcNow();
                var wait = RetryDelay(error, now);
                _retryNotBefore = now + wait;
                // Honor long provider cooldowns by stopping instead of retrying too early or waiting indefinitely.
                if (attempt >= MaxRateLimitRetries || wait > MaxInlineWait) throw QuotaExhausted();
            }
        }
    }

    private async Task WaitForBudgetAsync(int reservedTokens, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var now = _utcNow();
            while (_usage.TryPeek(out var oldest) && now - oldest.CompletedAt >= Window) _usage.Dequeue();
            var wait = _retryNotBefore > now ? _retryNotBefore - now : TimeSpan.Zero;
            if (_usage.Count >= _requestsPerMinute || _usage.Sum(entry => (long)entry.Tokens) + reservedTokens > _tokensPerMinute)
            {
                var budgetWait = _usage.Peek().CompletedAt + Window - now;
                if (budgetWait > wait) wait = budgetWait;
            }
            if (wait <= TimeSpan.Zero) return;
            if (wait > MaxInlineWait) throw QuotaExhausted();
            await _delay(wait, token);
        }
    }

    private static TimeSpan RetryDelay(ClientResultException error, DateTimeOffset now)
    {
        var response = error.GetRawResponse();
        if (response?.Headers.TryGetValue("Retry-After", out var header) == true)
        {
            if (int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(Math.Max(1, seconds));
            if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date > now ? date - now : TimeSpan.FromSeconds(1);
        }
        return Window;
    }

    private static ChatInputException QuotaExhausted() => new(
        "Gemini embedding quota is exhausted, not the app's chat limit. " +
        "Check embedding TPM/RPM and daily quota in AI Studio. The new manual was not published; " +
        "an existing published manual remains active. Try again after quota is available.", 429);
}
