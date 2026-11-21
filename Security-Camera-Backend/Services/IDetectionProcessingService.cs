namespace Security_Camera_Backend.Services;

/// <summary>
/// Reusable CV + persistence pipeline. Shared by the Raspberry Pi ingestion endpoint and
/// <c>POST /api/events/analyze</c>.
/// </summary>
public interface IDetectionProcessingService
{
    /// <summary>
    /// Analyses an already-saved frame, discards false positives and persists verified detections
    /// for the supplied camera system. A known <paramref name="clientEventId"/> returns the
    /// existing event instead of inserting a duplicate.
    /// </summary>
    /// <param name="rawFilePath">Full path to the frame on the shared captures volume.</param>
    /// <param name="capturedAt">Original capture time; only applied when <paramref name="isOfflineSync"/> is true.</param>
    /// <param name="isOfflineSync">True when the Pi is flushing its offline queue.</param>
    /// <param name="systemId">Camera system that owns the frame.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="clientEventId">Optional idempotency key, unique per system.</param>
    Task<DetectionProcessingResult> ProcessCapturedImageAsync(
        string rawFilePath,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        CancellationToken ct,
        Guid? clientEventId = null);
}
