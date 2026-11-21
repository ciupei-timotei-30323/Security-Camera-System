namespace Security_Camera_Backend.Services;

/// <summary>
/// Reusable CV + persistence pipeline. Shared by the Raspberry Pi ingestion endpoint and
/// <c>POST /api/events/analyze</c>.
/// </summary>
public interface IDetectionProcessingService
{
    /// <summary>
    /// Analyses an already-saved frame, discards false positives and persists verified detections
    /// for the supplied camera system.
    /// </summary>
    /// <param name="rawFilePath">Full path to the frame on the shared captures volume.</param>
    /// <param name="capturedAt">Original capture time for offline-queued frames; defaults to now (UTC).</param>
    /// <param name="isOfflineSync">True when the Pi is flushing its offline queue.</param>
    /// <param name="systemId">Camera system that owns the frame.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DetectionProcessingResult> ProcessCapturedImageAsync(
        string rawFilePath,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        CancellationToken ct);
}
