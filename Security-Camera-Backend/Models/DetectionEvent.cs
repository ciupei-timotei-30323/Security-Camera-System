namespace Security_Camera_Backend.Models;

public class DetectionEvent
{
    public int Id { get; set; }

    /// <summary>Camera system that produced this entrance log; every read filters on it.</summary>
    public string SystemId { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; }
    
    public bool IsSuspect { get; set; }
    
    public float? Confidence { get; set; }
    
    public string? DetectedFaceUri { get; set; }
    
    public string? FullImageUri { get; set; }
    
    public bool IsOfflineSync { get; set; }
    
    public bool IsEmailSent { get; set; }
    
}