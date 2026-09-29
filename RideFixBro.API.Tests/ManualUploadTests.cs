using RideFixBro.API.Services;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RideFixBro.API.Tests;

public class ManualUploadTests
{
    [Fact]
    public async Task SearchRetriesANewerPublishedRevisionWithoutFalseNoMatch()
    {
        var revisions = new Queue<string?>(["old", "new"]);
        var searched = new List<string>();
        var answer = await VectorDbService.ReadPublishedManualAsync(
            () => Task.FromResult(revisions.Dequeue()),
            revision =>
            {
                searched.Add(revision);
                return Task.FromResult(revision == "new" ? "New manual passage" : null);
            }, CancellationToken.None);
        Assert.Equal("New manual passage", answer);
        Assert.Equal(new[] { "old", "new" }, searched);
    }

    [Fact]
    public async Task StableEmptySearchDoesNotRetryAndMissingManualDoesNotSearch()
    {
        var searches = 0;
        var empty = await VectorDbService.ReadPublishedManualAsync(
            () => Task.FromResult<string?>("same"),
            _ => { searches++; return Task.FromResult<string?>(null); }, CancellationToken.None);
        Assert.Contains("No relevant", empty);
        Assert.Equal(1, searches);
        var missing = await VectorDbService.ReadPublishedManualAsync(
            () => Task.FromResult<string?>(null),
            _ => throw new InvalidOperationException("No search expected."), CancellationToken.None);
        Assert.Contains("No published", missing);
    }

    [Fact]
    public async Task ContinuousRepublicationStopsAfterTwoSearches()
    {
        var version = 0;
        var searches = 0;
        var error = await Assert.ThrowsAsync<ChatInputException>(() => VectorDbService.ReadPublishedManualAsync(
            () => Task.FromResult<string?>($"v{++version}"),
            _ => { searches++; return Task.FromResult<string?>(null); }, CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(2, searches);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnotherRevisionRead()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VectorDbService.ReadPublishedManualAsync(
            () => { reads++; return Task.FromResult<string?>("v1"); },
            _ => { cancellation.Cancel(); return Task.FromResult<string?>(null); }, cancellation.Token));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidPageSkipIsRejected(int skip)
    {
        Assert.Throws<ChatInputException>(() => VectorDbService.ExtractChunks(Pdf("first", "second"), skip, CancellationToken.None));
    }

    [Fact]
    public void EmptyTextAndInvalidFilesAreRejectedBeforeVectorWrites()
    {
        Assert.Throws<ChatInputException>(() => VectorDbService.ExtractChunks(Pdf(""), 0, CancellationToken.None));
        Assert.Throws<ChatInputException>(() => VectorDbService.ExtractChunks(Encoding.UTF8.GetBytes("not pdf"), 0, CancellationToken.None));
        Assert.Throws<ChatInputException>(() => VectorDbService.ExtractChunks(new byte[VectorDbService.MaxPdfBytes + 1], 0, CancellationToken.None));
    }

    internal static byte[] Pdf(params string[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(600, 800);
            if (text.Length > 0) page.AddText(text, 12, new PdfPoint(10, 700), font);
        }
        return builder.Build();
    }
}
