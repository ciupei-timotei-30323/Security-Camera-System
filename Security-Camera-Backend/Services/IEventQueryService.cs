using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services;

/// <summary>Tenant-scoped reads of entrance logs.</summary>
public interface IEventQueryService
{
    Task<IReadOnlyList<DetectionEventResponse>> GetRecentAsync(
        string systemId,
        int limit,
        CancellationToken ct);
}
