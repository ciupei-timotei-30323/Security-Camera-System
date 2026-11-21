using Security_Camera_Backend.Services.Camera;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

public sealed class LatestFrameStoreTests
{
    [Fact]
    public void TryGetLatest_ReturnsNullAfterTenSeconds()
    {
        var store = new LatestFrameStore();
        var now = DateTimeOffset.UtcNow;

        store.Set("sys-001", [1, 2, 3], now.AddSeconds(-11));

        Assert.False(store.TryGetLatest("sys-001", now, out _));

        store.Set("sys-001", [4, 5, 6], now.AddSeconds(-9));

        Assert.True(store.TryGetLatest("sys-001", now, out var frame));
        Assert.Equal([4, 5, 6], frame.Jpeg);
    }

    [Fact]
    public void Set_KeepsOnlyTheNewestFramePerSystem()
    {
        var store = new LatestFrameStore();
        var now = DateTimeOffset.UtcNow;

        store.Set("sys-001", [1], now);
        store.Set("sys-001", [2], now);
        store.Set("sys-002", [3], now);

        Assert.True(store.TryGetLatest("sys-001", now, out var sys1));
        Assert.True(store.TryGetLatest("sys-002", now, out var sys2));
        Assert.Equal([2], sys1.Jpeg);
        Assert.Equal([3], sys2.Jpeg);
    }

    [Fact]
    public void FrameStreamLimiter_CapsConcurrentStreamsPerSystem()
    {
        var limiter = new FrameStreamLimiter();
        var leases = new List<IDisposable>();

        try
        {
            for (var i = 0; i < FrameStreamLimiter.MaxConcurrentStreamsPerSystem; i++)
            {
                Assert.True(limiter.TryAcquire("sys-001", out var lease));
                leases.Add(lease);
            }

            Assert.False(limiter.TryAcquire("sys-001", out _));
            Assert.True(limiter.TryAcquire("sys-002", out var otherSystemLease));
            otherSystemLease.Dispose();

            leases[0].Dispose();
            leases.RemoveAt(0);

            Assert.True(limiter.TryAcquire("sys-001", out var replacement));
            leases.Add(replacement);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }
}
