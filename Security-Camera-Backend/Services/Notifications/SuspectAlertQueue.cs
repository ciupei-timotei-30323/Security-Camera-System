using System.Threading.Channels;

namespace Security_Camera_Backend.Services.Notifications;

/// <summary>Bounded in-memory queue that decouples event ingestion from outbound alerting.</summary>
public sealed class SuspectAlertQueue
{
    private readonly Channel<SuspectAlert> _channel =
        Channel.CreateBounded<SuspectAlert>(new BoundedChannelOptions(1_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public bool TryEnqueue(SuspectAlert alert) => _channel.Writer.TryWrite(alert);

    public IAsyncEnumerable<SuspectAlert> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
