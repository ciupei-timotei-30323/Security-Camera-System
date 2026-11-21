using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services;

/// <summary>Tenant-scoped reads and deletes of entrance logs.</summary>
public interface IEventQueryService
{
    Task<PagedEventsResponse> GetPagedAsync(string systemId, EventQuery query, CancellationToken ct);

    Task<DetectionEventResponse?> GetByIdAsync(string systemId, int eventId, CancellationToken ct);

    /// <summary>Returns the tracked entity for tenant-scoped file access, or <c>null</c>.</summary>
    Task<Models.DetectionEvent?> GetEntityAsync(string systemId, int eventId, CancellationToken ct);

    /// <summary>Deletes the event and its image files from disk. Returns false when not found.</summary>
    Task<bool> DeleteAsync(string systemId, int eventId, CancellationToken ct);
}
