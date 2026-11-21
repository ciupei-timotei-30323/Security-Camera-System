using System.Collections.Concurrent;

namespace Security_Camera_Backend.Services.Camera;

/// <summary>An immutable snapshot of the newest camera frame for one system.</summary>
public sealed record LatestFrame(byte[] Jpeg, DateTimeOffset CapturedAt, long Version);

public interface ILatestFrameStore
{
    /// <summary>Replaces the only retained frame for <paramref name="systemId"/>.</summary>
    void Set(string systemId, byte[] jpeg, DateTimeOffset capturedAt);

    /// <summary>Returns the latest frame when it is no older than 10 seconds.</summary>
    bool TryGetLatest(string systemId, out LatestFrame frame);

    /// <summary>Time-aware overload used by tests.</summary>
    bool TryGetLatest(string systemId, DateTimeOffset now, out LatestFrame frame);
}

/// <summary>
/// Process-local, thread-safe latest-frame cache. It intentionally keeps exactly one JPEG per
/// system instead of buffering a stream, so memory use is bounded by the number of systems.
/// </summary>
public sealed class LatestFrameStore : ILatestFrameStore
{
    public static readonly TimeSpan MaxFrameAge = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, LatestFrame> _frames =
        new(StringComparer.Ordinal);

    private long _version;

    public void Set(string systemId, byte[] jpeg, DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);
        ArgumentNullException.ThrowIfNull(jpeg);

        var version = Interlocked.Increment(ref _version);
        _frames[systemId] = new LatestFrame(jpeg, capturedAt, version);
    }

    public bool TryGetLatest(string systemId, out LatestFrame frame) =>
        TryGetLatest(systemId, DateTimeOffset.UtcNow, out frame);

    public bool TryGetLatest(string systemId, DateTimeOffset now, out LatestFrame frame)
    {
        if (!_frames.TryGetValue(systemId, out var candidate) ||
            now - candidate.CapturedAt > MaxFrameAge)
        {
            frame = null!;
            return false;
        }

        frame = candidate;
        return true;
    }
}
