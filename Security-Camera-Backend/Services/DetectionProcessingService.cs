using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Cv;

namespace Security_Camera_Backend.Services;

public sealed class DetectionProcessingService(
    SecurityCameraBackendContext dbContext,
    ICvServiceClient cvServiceClient,
    ISystemStateService systemStateService,
    INotificationDispatcher notificationDispatcher,
    ICaptureStorage captureStorage,
    IOptions<StorageOptions> storageOptions,
    ILogger<DetectionProcessingService> logger) : IDetectionProcessingService
{
    private readonly StorageOptions _storage = storageOptions.Value;

    public async Task<DetectionProcessingResult> ProcessCapturedImageAsync(
        string rawFilePath,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawFilePath);

        CvAnalysisResult analysis;

        try
        {
            analysis = await cvServiceClient.AnalyzeImageAsync(rawFilePath, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CV analysis failed for {RawFilePath}; removing orphaned frame.", rawFilePath);
            captureStorage.TryDelete(rawFilePath);
            throw;
        }

        if (!analysis.PersonDetected && !analysis.FaceDetected)
        {
            logger.LogInformation(
                "No person or face detected in {RawFilePath} (confidence: {Confidence}); discarding frame.",
                rawFilePath, analysis.Confidence);

            captureStorage.TryDelete(rawFilePath);

            return DetectionProcessingResult.NoDetection();
        }

        var fullImageUrl = _storage.ToPublicUrl(rawFilePath);
        var detectedFaceUrl = string.IsNullOrWhiteSpace(analysis.FaceCropPath)
            ? null
            : _storage.ToPublicUrl(analysis.FaceCropPath);

        var detectionEvent = new DetectionEvent
        {
            // Npgsql's timestamptz requires a strictly UTC instant.
            Timestamp = capturedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
            IsSuspect = await systemStateService.IsArmedAsync(ct),
            Confidence = analysis.Confidence,
            DetectedFaceUri = detectedFaceUrl,
            FullImageUri = fullImageUrl,
            IsOfflineSync = isOfflineSync,
            IsEmailSent = false
        };

        try
        {
            dbContext.DetectionEvents.Add(detectionEvent);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to persist detection event for {RawFilePath}; removing newly created files.",
                rawFilePath);

            dbContext.Entry(detectionEvent).State = EntityState.Detached;
            captureStorage.TryDelete(rawFilePath);
            captureStorage.TryDelete(analysis.FaceCropPath);
            throw;
        }

        logger.LogInformation(
            "Recorded detection event {EventId} (suspect: {IsSuspect}, offline sync: {IsOfflineSync}) from {RawFilePath}",
            detectionEvent.Id, detectionEvent.IsSuspect, detectionEvent.IsOfflineSync, rawFilePath);

        if (detectionEvent.IsSuspect)
        {
            await DispatchSuspectAlertAsync(detectionEvent, ct);
        }

        return DetectionProcessingResult.Saved(detectionEvent);
    }

    private async Task DispatchSuspectAlertAsync(DetectionEvent detectionEvent, CancellationToken ct)
    {
        try
        {
            await notificationDispatcher.DispatchSuspectAlertAsync(detectionEvent, ct);
        }
        catch (Exception ex)
        {
            // The event is already persisted; a failed alert must not fail the upload.
            logger.LogError(ex, "Notification dispatch failed for detection event {EventId}.", detectionEvent.Id);
        }
    }
}
