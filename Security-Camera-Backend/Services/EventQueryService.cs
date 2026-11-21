using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

public sealed class EventQueryService(
    SecurityCameraBackendContext dbContext,
    ICaptureStorage captureStorage,
    ILogger<EventQueryService> logger) : IEventQueryService
{
    public async Task<PagedEventsResponse> GetPagedAsync(
        string systemId,
        EventQuery query,
        CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var eventsQuery = dbContext.DetectionEvents
            .AsNoTracking()
            .Where(detectionEvent => detectionEvent.SystemId == systemId);

        if (query.SuspectOnly)
        {
            eventsQuery = eventsQuery.Where(detectionEvent => detectionEvent.IsSuspect);
        }

        if (query.From is not null)
        {
            eventsQuery = eventsQuery.Where(detectionEvent => detectionEvent.Timestamp >= query.From.Value);
        }

        if (query.To is not null)
        {
            eventsQuery = eventsQuery.Where(detectionEvent => detectionEvent.Timestamp <= query.To.Value);
        }

        var total = await eventsQuery.CountAsync(ct);

        var events = await eventsQuery
            .OrderByDescending(detectionEvent => detectionEvent.Timestamp)
            .ThenByDescending(detectionEvent => detectionEvent.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedEventsResponse(
            events.Select(DetectionEventResponse.FromEntity).ToList(),
            total,
            page,
            pageSize);
    }

    public async Task<DetectionEventResponse?> GetByIdAsync(
        string systemId,
        int eventId,
        CancellationToken ct)
    {
        var detectionEvent = await GetEntityAsync(systemId, eventId, ct);

        return detectionEvent is null ? null : DetectionEventResponse.FromEntity(detectionEvent);
    }

    public Task<DetectionEvent?> GetEntityAsync(
        string systemId,
        int eventId,
        CancellationToken ct) =>
        dbContext.DetectionEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                detectionEvent => detectionEvent.Id == eventId && detectionEvent.SystemId == systemId,
                ct);

    public async Task<bool> DeleteAsync(string systemId, int eventId, CancellationToken ct)
    {
        var detectionEvent = await dbContext.DetectionEvents
            .SingleOrDefaultAsync(
                e => e.Id == eventId && e.SystemId == systemId,
                ct);

        if (detectionEvent is null)
        {
            return false;
        }

        dbContext.DetectionEvents.Remove(detectionEvent);
        await dbContext.SaveChangesAsync(ct);

        captureStorage.TryDelete(detectionEvent.FullImageUri);
        captureStorage.TryDelete(detectionEvent.DetectedFaceUri);
        captureStorage.TryDelete(detectionEvent.AnnotatedImageUri);

        logger.LogInformation(
            "Deleted detection event {EventId} for system {SystemId} and its capture files.",
            eventId, systemId);

        return true;
    }
}
