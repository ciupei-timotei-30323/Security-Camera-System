using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Cv;

namespace Security_Camera_Backend.Services;

public interface ISystemStatusService
{
    Task<SystemStatusResponse> GetAsync(string systemId, CancellationToken ct);
}

public sealed class SystemStatusService(
    SecurityCameraBackendContext dbContext,
    ICvServiceHealthService cvServiceHealthService,
    TimeProvider timeProvider) : ISystemStatusService
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);

    public async Task<SystemStatusResponse> GetAsync(string systemId, CancellationToken ct)
    {
        var devices = await dbContext.PiDevices
            .AsNoTracking()
            .Where(device => device.SystemId == systemId)
            .Select(device => new DeviceSnapshot(device.DeviceType, device.LastSeenAt))
            .ToListAsync(ct);

        var now = timeProvider.GetUtcNow();

        return new SystemStatusResponse(
            BuildDeviceStatus(LatestLastSeen(devices, DeviceType.Camera), now),
            BuildDeviceStatus(LatestLastSeen(devices, DeviceType.AlertTerminal), now),
            "ok",
            await cvServiceHealthService.GetStatusAsync(ct));
    }

    private static DateTime? LatestLastSeen(
        IEnumerable<DeviceSnapshot> devices,
        DeviceType deviceType)
    {
        DateTime? latest = null;

        foreach (var device in devices)
        {
            if (device.DeviceType != deviceType || device.LastSeenAt is not DateTime lastSeen)
            {
                continue;
            }

            if (latest is null || lastSeen > latest)
            {
                latest = lastSeen;
            }
        }

        return latest;
    }

    private sealed record DeviceSnapshot(DeviceType DeviceType, DateTime? LastSeenAt);

    private static DeviceStatusResponse BuildDeviceStatus(DateTime? lastSeenAt, DateTimeOffset now)
    {
        var lastSeen = lastSeenAt is null
            ? (DateTimeOffset?)null
            : new DateTimeOffset(DateTime.SpecifyKind(lastSeenAt.Value, DateTimeKind.Utc));

        return new DeviceStatusResponse(
            lastSeen is not null && now - lastSeen.Value <= OnlineWindow,
            lastSeen);
    }
}
