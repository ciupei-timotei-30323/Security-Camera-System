namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Describes where captures are written on the shared Docker volume.
/// Bound from <c>Storage</c> (env var: <c>Storage__CapturesDirectory</c>).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string CapturesDirectory { get; set; } = "/data/captures";

    /// <summary>Absolute, normalized captures directory.</summary>
    public string GetCapturesDirectoryFullPath() => Path.GetFullPath(CapturesDirectory);
}
