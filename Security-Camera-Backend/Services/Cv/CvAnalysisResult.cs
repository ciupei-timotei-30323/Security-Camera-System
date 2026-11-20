using System.Text.Json.Serialization;

namespace Security_Camera_Backend.Services.Cv;

/// <summary>Response payload returned by <c>cv-service</c> on <c>POST /analyze</c>.</summary>
public sealed class CvAnalysisResult
{
    [JsonPropertyName("person_detected")]
    public bool PersonDetected { get; init; }

    [JsonPropertyName("face_detected")]
    public bool FaceDetected { get; init; }

    [JsonPropertyName("confidence")]
    public float? Confidence { get; init; }

    /// <summary>Local path on the shared volume written by <c>cv-service</c>, e.g. <c>/data/captures/face_123.jpg</c>.</summary>
    [JsonPropertyName("face_crop_path")]
    public string? FaceCropPath { get; init; }
}
