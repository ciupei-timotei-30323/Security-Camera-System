using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

/// <summary>Outcome of running a capture through CV analysis and Neon persistence.</summary>
public sealed record DetectionProcessingResult(bool Persisted, DetectionEvent? DetectionEvent)
{
    public static DetectionProcessingResult NoDetection() => new(false, null);

    public static DetectionProcessingResult Saved(DetectionEvent detectionEvent) => new(true, detectionEvent);
}
