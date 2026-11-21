namespace Security_Camera_Backend.Services.Notifications;

/// <summary>Snapshot of a persisted suspect event, safe to hand to the background dispatcher.</summary>
public sealed record SuspectAlert(
    int EventId,
    string SystemId,
    DateTimeOffset Timestamp,
    float? Confidence,
    string? DetectedFaceUri,
    string? AnnotatedImageUri,
    string? FullImageUri);
