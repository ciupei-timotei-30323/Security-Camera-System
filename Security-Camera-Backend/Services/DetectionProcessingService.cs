using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Cv;

namespace Security_Camera_Backend.Services;

public sealed class DetectionProcessingService(
    SecurityCameraBackendContext dbContext,
    ICvServiceClient cvServiceClient,
    ISystemStateService systemStateService,
    IAlarmStateStore alarmStateStore,
    INotificationDispatcher notificationDispatcher,
    ICaptureStorage captureStorage,
    ILogger<DetectionProcessingService> logger) : IDetectionProcessingService
{
    public async Task<DetectionProcessingResult> ProcessCapturedImageAsync(
        string rawFilePath,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        string systemId,
        CancellationToken ct,
        Guid? clientEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawFilePath);

        if (clientEventId is not null)
        {
            var existing = await FindByClientEventIdAsync(systemId, clientEventId.Value, ct);

            if (existing is not null)
            {
                // A retry of an already-processed frame: keep the existing row and drop the upload.
                captureStorage.TryDelete(rawFilePath);
                return DetectionProcessingResult.Existing(existing);
            }
        }

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

        var timestamp = isOfflineSync && capturedAt is not null
            ? capturedAt.Value.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        var detectionEvent = new DetectionEvent
        {
            SystemId = systemId,
            // Npgsql's timestamptz requires a strictly UTC instant.
            Timestamp = timestamp,
            IsSuspect = await systemStateService.IsArmedAsync(systemId, timestamp, ct),
            Confidence = analysis.Confidence,
            DetectedFaceUri = ToStoredFileName(analysis.FaceCropPath),
            FullImageUri = ToStoredFileName(rawFilePath),
            AnnotatedImageUri = ToStoredFileName(analysis.AnnotatedImagePath),
            IsOfflineSync = isOfflineSync,
            IsEmailSent = false,
            ClientEventId = clientEventId
        };

        try
        {
            dbContext.DetectionEvents.Add(detectionEvent);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (clientEventId is not null)
        {
            // A concurrent retry inserted the same (systemId, clientEventId) first.
            dbContext.Entry(detectionEvent).State = EntityState.Detached;
            dbContext.ChangeTracker.Clear();

            var existing = await FindByClientEventIdAsync(systemId, clientEventId.Value, ct);

            if (existing is not null)
            {
                logger.LogInformation(
                    "Concurrent idempotent retry for {ClientEventId} in system {SystemId}; returning event {EventId}.",
                    clientEventId, systemId, existing.Id);

                DeleteNewFiles(rawFilePath, analysis);
                return DetectionProcessingResult.Existing(existing);
            }

            logger.LogError(ex,
                "Failed to persist detection event for {RawFilePath}; removing newly created files.",
                rawFilePath);

            dbContext.ChangeTracker.Clear();
            DeleteNewFiles(rawFilePath, analysis);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to persist detection event for {RawFilePath}; removing newly created files.",
                rawFilePath);

            dbContext.Entry(detectionEvent).State = EntityState.Detached;
            DeleteNewFiles(rawFilePath, analysis);
            throw;
        }

        logger.LogInformation(
            "Recorded detection event {EventId} for system {SystemId} (suspect: {IsSuspect}, offline sync: {IsOfflineSync}) from {RawFilePath}",
            detectionEvent.Id, detectionEvent.SystemId, detectionEvent.IsSuspect,
            detectionEvent.IsOfflineSync, rawFilePath);

        if (detectionEvent.IsSuspect)
        {
            // The terminal alarm must react immediately; only the e-mail fan-out is queued.
            alarmStateStore.Trigger(detectionEvent.SystemId);
            await DispatchSuspectAlertAsync(detectionEvent, ct);
        }

        return DetectionProcessingResult.Saved(detectionEvent);
    }

    private Task<DetectionEvent?> FindByClientEventIdAsync(
        string systemId,
        Guid clientEventId,
        CancellationToken ct) =>
        dbContext.DetectionEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SystemId == systemId && e.ClientEventId == clientEventId,
                ct);

    private void DeleteNewFiles(string rawFilePath, CvAnalysisResult analysis)
    {
        captureStorage.TryDelete(rawFilePath);
        captureStorage.TryDelete(analysis.FaceCropPath);
        captureStorage.TryDelete(analysis.AnnotatedImagePath);
    }

    private static string? ToStoredFileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);

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
