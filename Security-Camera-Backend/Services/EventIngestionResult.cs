namespace Security_Camera_Backend.Services;

/// <summary>Outcome of processing a single Raspberry Pi frame.</summary>
public sealed record EventIngestionResult(
    int? EventId,
    bool PersonDetected,
    DetectionProcessingResult? ProcessingResult = null,
    bool IsDuplicate = false)
{
    public static EventIngestionResult NoPersonDetected() => new(null, false);
}
