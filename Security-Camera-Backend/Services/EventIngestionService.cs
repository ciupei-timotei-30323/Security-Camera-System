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
        string systemId,
        CancellationToken ct)
    {
        var rawImagePath = await captureStorage.SaveFrameAsync(image, ct);

        logger.LogInformation(
            "Saved motion frame for system {SystemId} to {RawImagePath}",
            systemId, rawImagePath);

        var processingResult = await detectionProcessingService.ProcessCapturedImageAsync(
            rawImagePath, capturedAt, isOfflineSync, systemId, ct);

        return processingResult is { Persisted: true, DetectionEvent: not null }
            ? new EventIngestionResult(processingResult.DetectionEvent.Id, true)
            : EventIngestionResult.NoPersonDetected();
    }
}
