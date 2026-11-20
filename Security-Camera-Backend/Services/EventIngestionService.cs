namespace Security_Camera_Backend.Services;

public sealed class EventIngestionService(
    ICaptureStorage captureStorage,
    IDetectionProcessingService detectionProcessingService,
    ILogger<EventIngestionService> logger) : IEventIngestionService
{
    public async Task<EventIngestionResult> IngestAsync(
        IFormFile? image,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        CancellationToken ct)
    {
        var rawImagePath = await captureStorage.SaveFrameAsync(image, ct);

        logger.LogInformation("Saved motion frame to {RawImagePath}", rawImagePath);

        var processingResult = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath, capturedAt, isOfflineSync, ct);

        return processingResult is { Persisted: true, DetectionEvent: not null }
            ? new EventIngestionResult(processingResult.DetectionEvent.Id, true)
            : EventIngestionResult.NoPersonDetected();
    }
}
