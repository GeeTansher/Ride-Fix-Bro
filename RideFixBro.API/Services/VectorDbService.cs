using AutoGen.Core;
using OpenAI.Embeddings;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RideFixBro.API.Agents;
using RideFixBro.API.Configuration;
using System.Security.Cryptography;
using System.Text;
using UglyToad.PdfPig;
using static Qdrant.Client.Grpc.Conditions;

namespace RideFixBro.API.Services;

// partial: AutoGen [Function] wale method ka contract/wrapper isi class ke generated part mein add karta hai.
// Ek shared collection mein do tarah ke points hain:
//   chunk    = PDF ke text ka tukda + uska embedding vector.
//   manifest = ek ManualKey ka chhota record: kaunsi complete upload revision ab active hai.
// ManualKey catalog ki stable identity hai; revision har upload ka naya version ID hai.
public partial class VectorDbService : IManualPublisher
{
    public const int MaxPdfBytes = 20 * 1024 * 1024;
    private const int Dimensions = 3072;
    private const int MaxPages = 1000;
    private const int MaxChunks = 2000;
    private readonly QdrantClient _qdrantClient;
    private readonly EmbeddingClient _embeddingClient;
    private readonly ManualEmbeddingClient _manualEmbeddings;
    private readonly string _collectionName;
    // Same process mein ek upload; multiple app instances ke liye controller SQL publication lock bhi leta hai.
    private readonly SemaphoreSlim _uploadGate = new(1, 1);

    public VectorDbService(IConfiguration config)
    {
        var host = config["Qdrant_Vector_DB:Cluster_Endpoint"];
        var key = config["Qdrant_Vector_DB:API_Key"];
        var geminiKey = config["API_Keys:Gemini_Api_key"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(geminiKey))
            throw new InvalidOperationException("Qdrant and Gemini credentials are required.");
        var collectionName = config["Qdrant_Vector_DB:ManualCollection"];
        if (string.IsNullOrWhiteSpace(collectionName))
            throw new InvalidOperationException("Qdrant_Vector_DB:ManualCollection is required.");
        // Collection ek explicit config value hai, bike name se derive/default nahi hoti.
        _collectionName = collectionName;
        _qdrantClient = new QdrantClient(host: host, https: true, apiKey: key,
            grpcTimeout: TimeSpan.FromSeconds(ApiTimeouts.Seconds));
        _embeddingClient = OpenAIClientBuilder.Create(geminiKey).GetEmbeddingClient("gemini-embedding-2-preview");
        // Upload-only budget below the project's 100 RPM / 30k TPM, leaving room for search.
        // Azure overrides: EmbeddingUpload__RequestsPerMinute / EmbeddingUpload__TokensPerMinute.
        // Daily quota is still enforced by Google; waiting cannot restore an exhausted daily allowance.
        _manualEmbeddings = new ManualEmbeddingClient(_embeddingClient,
            config.GetValue("EmbeddingUpload:RequestsPerMinute", 80),
            config.GetValue("EmbeddingUpload:TokensPerMinute", 24_000));
    }

    // Ye real method hi [Function] hai, dummy overload nahi. MechanicBroAgent generated schema se key/token hata kar
    // sirf userQuery model ko dikhata hai; custom execution map owned chat ki key aur HTTP cancellation token deta hai.
    /// <summary>
    /// Search the selected motorcycle's verified manual for technical details.
    /// Manual identity is supplied by the server, not by the model.
    /// </summary>
    /// <param name="userQuery">Question to look up in the selected manual.</param>
    /// <param name="manualKey">Trusted ManualKey from the selected catalog entry.</param>
    /// <param name="cancellationToken">Server request cancellation signal.</param>
    [Function]
    public async Task<string> SearchManualAsync(string userQuery, string manualKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(manualKey);
        return await ReadPublishedManualAsync(
            () => GetPublishedRevisionAsync(manualKey, cancellationToken),
            revision => SearchRevisionAsync(userQuery, manualKey, revision, cancellationToken),
            cancellationToken);
    }

    // Keep this small consistency rule separate from network I/O: a publish can delete a reader's old revision.
    internal static async Task<string> ReadPublishedManualAsync(
        Func<Task<string?>> readRevision, Func<string, Task<string?>> readPassages, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var revision = await readRevision();
        token.ThrowIfCancellationRequested();
        if (revision is null)
            return "No published manual exists for this catalog entry. Do not use another model/year's manual.";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var passages = await readPassages(revision);
            token.ThrowIfCancellationRequested();
            if (passages is not null) return passages;
            var current = await readRevision();
            token.ThrowIfCancellationRequested();
            if (current == revision)
                return "No relevant manual passages were found. Do not present general advice as a verified manual specification.";
            if (current is null)
                throw new ChatInputException("Manual publication changed during search. Try again.", 503);
            revision = current;
        }
        throw new ChatInputException("Manual is being updated. Try again shortly.", 503);
    }

    private async Task<string?> GetPublishedRevisionAsync(string manualKey, CancellationToken token)
    {
        // Manifest se sirf active version read hota hai, PDF text/similarity search nahi.
        var manifests = await _qdrantClient.RetrieveAsync(_collectionName, ManifestId(manualKey),
            withPayload: true, cancellationToken: token);
        var manifest = manifests.SingleOrDefault();
        if (manifest is null) return null;
        if (!manifest.Payload.TryGetValue("revision", out var revision) || string.IsNullOrWhiteSpace(revision.StringValue))
            throw new InvalidOperationException("Manual publication metadata is invalid.");
        return revision.StringValue;
    }

    private async Task<string?> SearchRevisionAsync(string query, string manualKey, string revision, CancellationToken token)
    {
        var embedding = await _embeddingClient.GenerateEmbeddingAsync(query, cancellationToken: token);
        // Key + active revision + chunk: doosri bikes, incomplete uploads aur manifest results se excluded hain.
        var results = await _qdrantClient.QueryAsync(_collectionName, embedding.Value.ToFloats().ToArray(),
            filter: new Filter { Must = { MatchKeyword("manual_key", manualKey), MatchKeyword("revision", revision),
                MatchKeyword("kind", "chunk") } },
            limit: 3, cancellationToken: token);
        var passages = results.Where(result => result.Payload.TryGetValue("text", out var value) &&
            !string.IsNullOrWhiteSpace(value.StringValue)).Select(result => result.Payload["text"].StringValue).ToArray();
        return passages.Length > 0 ? string.Join("\n---\n", passages) : null;
    }

    public async Task<ManualUploadResult> ReplaceManualAsync(byte[] pdf, string manualKey, int skipPages, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manualKey);
        if (!await _uploadGate.WaitAsync(0, token))
            throw new ChatInputException("Another manual upload is running. Retry afterwards.", 409);
        try
        {
            // Parse -> stage -> publish -> cleanup. Publish se pehle failure aaye toh old manual active rehti hai.
            var chunks = ExtractChunks(pdf, skipPages, token);
            var revision = Guid.NewGuid().ToString("N");
            await EnsureCollectionAsync(token);
            await UploadChunksAsync(manualKey, revision, chunks, token);
            await PublishRevisionAsync(manualKey, revision, token);
            await RemoveOldRevisionsAsync(manualKey, revision, token);
            return new(manualKey, chunks.Count, skipPages);
        }
        finally { _uploadGate.Release(); }
    }

    private async Task EnsureCollectionAsync(CancellationToken token)
    {
        var collections = await _qdrantClient.ListCollectionsAsync(cancellationToken: token);
        if (!collections.Contains(_collectionName))
            await _qdrantClient.CreateCollectionAsync(_collectionName,
                new VectorParams { Size = Dimensions, Distance = Distance.Cosine }, cancellationToken: token);
        // Strict-mode filtering needs these Qdrant payload indexes, not SQL indexes.
        foreach (var field in new[] { "manual_key", "revision", "kind" })
            await _qdrantClient.CreatePayloadIndexAsync(_collectionName, field, PayloadSchemaType.Keyword,
                wait: true, cancellationToken: token);
    }

    private async Task UploadChunksAsync(string manualKey, string revision, IReadOnlyList<string> chunks, CancellationToken token)
    {
        for (var index = 0; index < chunks.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            // Wait/retry only this embedding when its provider budget is exhausted; Qdrant is not the limiter.
            var embedding = await _manualEmbeddings.GenerateAsync(chunks[index], token);
            await _qdrantClient.UpsertAsync(_collectionName, new[]
            {
                new PointStruct
                {
                    Id = Guid.NewGuid(), Vectors = embedding.ToArray(),
                    Payload = { ["manual_key"] = manualKey, ["revision"] = revision, ["kind"] = "chunk",
                        ["text"] = chunks[index], ["chunk_number"] = index + 1 }
                }
            }, wait: true, cancellationToken: token);
        }
    }

    private Task PublishRevisionAsync(string manualKey, string revision, CancellationToken token) =>
        // One metadata point is the publication switch. Its zero vector is excluded by kind=chunk.
        _qdrantClient.UpsertAsync(_collectionName, new[]
        {
            new PointStruct
            {
                Id = ManifestId(manualKey), Vectors = new float[Dimensions],
                Payload = { ["manual_key"] = manualKey, ["revision"] = revision, ["kind"] = "manifest" }
            }
        }, wait: true, cancellationToken: token);

    private Task RemoveOldRevisionsAsync(string manualKey, string revision, CancellationToken token) =>
        // Same-key older/incomplete versions only; other keys and untagged legacy points stay untouched.
        // Cleanup failure surfaces as an error, but reads still use only the published revision.
        _qdrantClient.DeleteAsync(_collectionName, new Filter
        {
            Must = { MatchKeyword("manual_key", manualKey) },
            MustNot = { MatchKeyword("revision", revision) }
        }, wait: true, cancellationToken: token);

    // Same key ka hamesha same metadata ID; text chunks ke random IDs se alag, yahan prefixed key ka hash use hota hai.
    // Ye sirf deterministic lookup ID hai, password/security hashing ka use case nahi.
    internal static Guid ManifestId(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("ridefix-manual:" + key)).AsSpan(0, 16));

    internal static List<string> ExtractChunks(byte[] pdf, int skipPages, CancellationToken token)
    {
        if (pdf.Length == 0 || pdf.Length > MaxPdfBytes)
            throw new ChatInputException("PDF must be between 1 byte and 20 MiB.", 413);
        if (pdf.Length < 5 || Encoding.ASCII.GetString(pdf, 0, 5) != "%PDF-")
            throw new ChatInputException("Upload a valid PDF file.");
        if (skipPages < 0) throw new ChatInputException("skipPages cannot be negative.");
        var chunks = new List<string>();
        try
        {
            using var document = PdfDocument.Open(pdf);
            if (document.NumberOfPages > MaxPages)
                throw new ChatInputException($"PDF exceeds the {MaxPages}-page limit.", 413);
            if (skipPages >= document.NumberOfPages)
                throw new ChatInputException("skipPages must leave at least one page.");
            // PDF pages 1-based hain: skipPages=7 ka matlab physical page 8 se shuru.
            // Har 300 extracted words ka chunk banta hai; page boundary par chunk todna zaroori nahi.
            var words = new List<string>(300);
            for (var page = skipPages + 1; page <= document.NumberOfPages; page++)
            {
                token.ThrowIfCancellationRequested();
                // Sirf PDF text layer read hoti hai. Images/diagrams embed ya OCR nahi kar rahe.
                foreach (var word in document.GetPage(page).GetWords())
                {
                    words.Add(word.Text);
                    if (words.Count == 300)
                    {
                        chunks.Add(string.Join(" ", words));
                        words.Clear();
                        if (chunks.Count > MaxChunks)
                            throw new ChatInputException("PDF contains too much text; split the manual.", 413);
                    }
                }
            }
            if (words.Count > 0) chunks.Add(string.Join(" ", words));
            if (chunks.Count > MaxChunks)
                throw new ChatInputException("PDF contains too much text; split the manual.", 413);
        }
        catch (UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            throw new ChatInputException("PDF is malformed, encrypted, or unsupported.");
        }
        if (chunks.Count == 0)
            throw new ChatInputException("No extractable text remains. Scanned/image-only PDFs require OCR before upload.");
        return chunks;
    }
}

public record ManualUploadResult(string ManualKey, int Chunks, int SkippedPages);
