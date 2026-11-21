using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

public sealed class EventQueryService(SecurityCameraBackendContext dbContext) : IEventQueryService
{
    public async Task<IReadOnlyList<DetectionEventResponse>> GetRecentAsync(
        string systemId,
        int limit,
        CancellationToken ct)
    {
        var events = await dbContext.DetectionEvents
            .AsNoTracking()
            .Where(detectionEvent => detectionEvent.SystemId == systemId)
            .OrderByDescending(detectionEvent => detectionEvent.Timestamp)
            .ThenByDescending(detectionEvent => detectionEvent.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return events.Select(DetectionEventResponse.FromEntity).ToList();
    }
}
