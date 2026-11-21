using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

public sealed class EventIngestionService(
    SecurityCameraBackendContext dbContext,
    ICaptureStorage captureStorage,
    IDetectionProcessingService detectionProcessingService,
    ILogger<EventIngestionService> logger) : IEventIngestionService
{
    public async Task<EventIngestionResult> IngestAsync(
        IFormFile? image,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        Guid? clientEventId,
        CancellationToken ct)
    {
        if (clientEventId is not null)
        {
            // Cheap pre-check before writing the upload to disk. The processing pipeline repeats
            // this check and also handles the unique-index race.
            var existing = await dbContext.DetectionEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    e => e.SystemId == systemId && e.ClientEventId == clientEventId,
                    ct);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Idempotent retry for client event {ClientEventId} in system {SystemId}; returning event {EventId}.",
                    clientEventId, systemId, existing.Id);

                return new EventIngestionResult(
                    existing.Id,
                    true,
                    DetectionProcessingResult.Existing(existing),
                    IsDuplicate: true);
            }
        }

        var rawImagePath = await captureStorage.SaveFrameAsync(image, ct);

        logger.LogInformation(
            "Saved motion frame for system {SystemId} to {RawImagePath}",
            systemId, rawImagePath);

        var processingResult = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath, capturedAt, isOfflineSync, systemId, ct, clientEventId);

        return processingResult is { Persisted: true, DetectionEvent: not null }
            ? new EventIngestionResult(
                processingResult.DetectionEvent.Id,
                true,
                processingResult,
                processingResult.IsDuplicate)
            : EventIngestionResult.NoPersonDetected();
    }
}
