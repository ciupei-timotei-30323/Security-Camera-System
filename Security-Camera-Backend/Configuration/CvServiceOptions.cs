namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Internal computer-vision microservice location.
/// Bound from <c>CvService</c> (env vars: <c>CvService__BaseUrl</c>, <c>CvService__TimeoutSeconds</c>).
/// </summary>
public sealed class CvServiceOptions
{
    public const string SectionName = "CvService";

    public string BaseUrl { get; set; } = "http://cv-service:8000";

    public int TimeoutSeconds { get; set; } = 10;
}
