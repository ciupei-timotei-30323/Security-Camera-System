using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Cv;

namespace Security_Camera_Backend.Services;

public sealed class EventIngestionService(
    SecurityCameraBackendContext dbContext,
    ICvServiceClient cvServiceClient,
    ISystemStateService systemStateService,
    INotificationDispatcher notificationDispatcher,
    IOptions<StorageOptions> storageOptions,
    ILogger<EventIngestionService> logger) : IEventIngestionService
{
    private static readonly HashSet<string> AllowedImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    private readonly StorageOptions _storage = storageOptions.Value;
    private readonly string _capturesRoot = EnsureTrailingSeparator(
        Path.GetFullPath(storageOptions.Value.CapturesDirectory));

    public async Task<EventIngestionResult> IngestAsync(
        IFormFile? image,
        DateTimeOffset? capturedAt,
        bool isOfflineSync,
        CancellationToken ct)
    {
        Validate(image);

        Directory.CreateDirectory(_capturesRoot);

        var rawImagePath = Path.Combine(
            _capturesRoot,
            $"raw_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.jpg");

        await SaveToDiskAsync(image!, rawImagePath, ct);

        logger.LogInformation("Saved motion frame to {RawImagePath}", rawImagePath);

        CvAnalysisResult analysis;

        try
        {
            analysis = await cvServiceClient.AnalyzeImageAsync(rawImagePath, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CV analysis failed for {RawImagePath}; removing orphaned frame.", rawImagePath);
            TryDeleteCaptureFile(rawImagePath);
            throw;
        }

        if (!analysis.PersonDetected)
        {
            logger.LogInformation(
                "No person detected in {RawImagePath} (face detected: {FaceDetected}, confidence: {Confidence}); discarding frame.",
                rawImagePath, analysis.FaceDetected, analysis.Confidence);

            TryDeleteCaptureFile(rawImagePath);
            TryDeleteCaptureFile(analysis.FaceCropPath);

            return EventIngestionResult.NoPersonDetected();
        }

        var faceCropPath = string.IsNullOrWhiteSpace(analysis.FaceCropPath)
            ? rawImagePath
            : analysis.FaceCropPath;

        var detectionEvent = new DetectionEvent
        {
            Timestamp = capturedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow,
            IsSuspect = await systemStateService.IsArmedAsync(ct),
            Confidence = analysis.Confidence,
            DetectedFaceUri = _storage.ToPublicUrl(faceCropPath),
            FullImageUri = _storage.ToPublicUrl(rawImagePath),
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
                "Failed to persist detection event for {RawImagePath}; removing newly created files.",
                rawImagePath);
            dbContext.Entry(detectionEvent).State = EntityState.Detached;
            TryDeleteCaptureFile(rawImagePath);
            TryDeleteCaptureFile(faceCropPath);
            throw;
        }

        logger.LogInformation(
            "Recorded detection event {EventId} (suspect: {IsSuspect}, offline sync: {IsOfflineSync}) from {RawImagePath}",
            detectionEvent.Id, detectionEvent.IsSuspect, detectionEvent.IsOfflineSync, rawImagePath);

        if (detectionEvent.IsSuspect)
        {
            await DispatchNotificationAsync(detectionEvent, ct);
        }

        return new EventIngestionResult(detectionEvent.Id, true);
    }

    private static void Validate(IFormFile? image)
    {
        if (image is null)
        {
            throw new ValidationAppException(
                "An image file is required in the 'image' form field.",
                "Missing multipart form field 'image'.");
        }

        if (image.Length <= 0)
        {
            throw new ValidationAppException(
                "The uploaded image is empty.",
                $"Uploaded file '{image.FileName}' has zero length.");
        }

        if (!AllowedImageContentTypes.Contains(image.ContentType))
        {
            throw new ValidationAppException(
                "Unsupported image content type. Allowed types: image/jpeg, image/png, image/webp.",
                $"Unsupported content type '{image.ContentType}' for file '{image.FileName}'.");
        }
    }

    private static async Task SaveToDiskAsync(IFormFile image, string destinationPath, CancellationToken ct)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await image.CopyToAsync(destination, ct);
    }

    private async Task DispatchNotificationAsync(DetectionEvent detectionEvent, CancellationToken ct)
    {
        try
        {
            await notificationDispatcher.DispatchAsync(detectionEvent, ct);
        }
        catch (Exception ex)
        {
            // The event is already persisted; a failed alert must not fail the Pi's upload.
            logger.LogError(ex, "Notification dispatch failed for detection event {EventId}.", detectionEvent.Id);
        }
    }

    /// <summary>
    /// Deletes a file on the shared volume. Paths outside the captures directory are ignored
    /// so a misbehaving CV response can never delete arbitrary files.
    /// </summary>
    private void TryDeleteCaptureFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(filePath);

            if (!fullPath.StartsWith(_capturesRoot, StringComparison.Ordinal))
            {
                logger.LogWarning("Refusing to delete '{FilePath}' because it is outside {CapturesRoot}.",
                    filePath, _capturesRoot);
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                logger.LogDebug("Deleted capture file {FilePath}", fullPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete capture file {FilePath}", filePath);
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
