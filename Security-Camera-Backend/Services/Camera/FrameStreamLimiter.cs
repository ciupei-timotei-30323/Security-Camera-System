using System.Collections.Concurrent;

namespace Security_Camera_Backend.Services.Camera;

/// <summary>Bounds how many MJPEG streams one system may hold open at once.</summary>
public interface IFrameStreamLimiter
{
    bool TryAcquire(string systemId, out IDisposable lease);
}

public sealed class FrameStreamLimiter : IFrameStreamLimiter
{
    public const int MaxConcurrentStreamsPerSystem = 5;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public bool TryAcquire(string systemId, out IDisposable lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        var gate = _gates.GetOrAdd(
            systemId,
            _ => new SemaphoreSlim(
                MaxConcurrentStreamsPerSystem,
                MaxConcurrentStreamsPerSystem));

        if (!gate.Wait(0))
        {
            lease = null!;
            return false;
        }

        lease = new StreamLease(gate);
        return true;
    }

    private sealed class StreamLease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
