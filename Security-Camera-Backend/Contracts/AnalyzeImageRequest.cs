namespace Security_Camera_Backend.Contracts;

/// <summary>JSON body accepted by <c>POST /api/events/analyze</c>.</summary>
public sealed record AnalyzeImageRequest(
    string? ImagePath,
    DateTimeOffset? CapturedAt = null,
    bool IsOfflineSync = false);
