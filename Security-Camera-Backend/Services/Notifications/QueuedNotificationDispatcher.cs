using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Notifications;

/// <summary>
/// Replaces the legacy synchronous dispatcher: ingestion only enqueues a snapshot and returns.
/// </summary>
public sealed class QueuedNotificationDispatcher(
    SuspectAlertQueue queue,
    ILogger<QueuedNotificationDispatcher> logger) : INotificationDispatcher
{
    public Task DispatchSuspectAlertAsync(DetectionEvent detectionEvent, CancellationToken ct = default)
    {
        var alert = new SuspectAlert(
            detectionEvent.Id,
            detectionEvent.SystemId,
            detectionEvent.Timestamp,
            detectionEvent.Confidence,
            detectionEvent.DetectedFaceUri,
            detectionEvent.AnnotatedImageUri,
            detectionEvent.FullImageUri);

        if (!queue.TryEnqueue(alert))
        {
            logger.LogWarning(
                "Suspect alert queue is full; dropping alert for event {EventId} in system {SystemId}.",
                detectionEvent.Id, detectionEvent.SystemId);
        }

        return Task.CompletedTask;
    }
}
