namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Describes where captures are written on the shared Docker volume and how they are exposed publicly.
/// Bound from <c>Storage</c> (env vars: <c>Storage__CapturesDirectory</c>, <c>Storage__PublicRequestPath</c>).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string CapturesDirectory { get; set; } = "/data/captures";

    public string PublicRequestPath { get; set; } = "/captures";

    /// <summary>Absolute, normalized captures directory.</summary>
    public string GetCapturesDirectoryFullPath() => Path.GetFullPath(CapturesDirectory);

    /// <summary>Normalized public request path, always starting with a single slash and without a trailing slash.</summary>
    public string GetPublicRequestPath() => "/" + PublicRequestPath.Trim('/');

    /// <summary>Converts a path on disk to the relative URL stored in PostgreSQL, e.g. <c>/captures/face_123.jpg</c>.</summary>
    public string ToPublicUrl(string filePath) =>
        $"{GetPublicRequestPath()}/{Path.GetFileName(filePath)}";
}
