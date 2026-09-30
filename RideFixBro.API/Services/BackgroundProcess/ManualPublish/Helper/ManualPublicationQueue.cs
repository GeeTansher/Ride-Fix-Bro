using System.Threading.Channels;

namespace RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;

// SQL stores the work. This channel only coalesces wake-up signals, so losing a signal cannot lose the PDF text.
public sealed class ManualPublicationQueue
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal SemaphoreSlim SubmissionGate { get; } = new(1, 1);
    public void Wake() => _wake.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken token) => await _wake.Reader.ReadAsync(token);
}
