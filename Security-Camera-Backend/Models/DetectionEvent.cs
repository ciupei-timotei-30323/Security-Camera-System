namespace Security_Camera_Backend.Models;

public class DetectionEvent
{
    public int Id { get; set; }
    
    public DateTime Timestamp { get; set; }
    
    public bool? IsSuspect { get; set; }
    
    public float? Confidence { get; set; }
    
    public Uri? DetectedFaceUri { get; set; }
    
    public Uri? FullImageUri { get; set; }
    
    public bool IsImageSynced { get; set; }
    
}