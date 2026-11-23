namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Request-size ceilings shared by Kestrel (<see cref="Services"/>) and the ingestion endpoints.
///
/// A motion frame is a single JPEG from a Pi camera (tens of KB); a full event upload is a single
/// photo. Anything larger is either a mistake or an attempt to exhaust the captures volume, so the
/// limits stay tight instead of relying on the multipart defaults.
/// </summary>
public static class RequestLimits
{
    /// <summary>Global Kestrel ceiling for any request body (10 MB).</summary>
    public const long MaxBodyBytes = 10 * 1024 * 1024;

    /// <summary><c>POST /api/events</c> and <c>POST /api/events/analyze</c> upload limit (10 MB).</summary>
    public const long MaxEventUploadBytes = 10 * 1024 * 1024;

    /// <summary><c>POST /api/camera/frame</c> upload limit (250 KB).</summary>
    public const long MaxFrameUploadBytes = 250 * 1024;
}
