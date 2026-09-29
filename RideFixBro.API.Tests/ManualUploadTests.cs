using RideFixBro.API.Services;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RideFixBro.API.Tests;

public class ManualUploadTests
{
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

    private static byte[] Pdf(params string[] pages)
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
