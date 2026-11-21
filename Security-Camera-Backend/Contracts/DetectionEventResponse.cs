using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Contracts;

/// <summary>Response returned when a detection was persisted to PostgreSQL.</summary>
public sealed record DetectionEventResponse(
    int Id,
    string SystemId,
    DateTimeOffset Timestamp,
    bool IsSuspect,
    float? Confidence,
    string? DetectedFaceUrl,
    string? FullImageUrl,
    bool IsOfflineSync)
{
    public static DetectionEventResponse FromEntity(DetectionEvent detectionEvent) => new(
        detectionEvent.Id,
        detectionEvent.SystemId,
        detectionEvent.Timestamp,
        detectionEvent.IsSuspect,
        detectionEvent.Confidence,
        detectionEvent.DetectedFaceUri,
        detectionEvent.FullImageUri,
        detectionEvent.IsOfflineSync);
}
