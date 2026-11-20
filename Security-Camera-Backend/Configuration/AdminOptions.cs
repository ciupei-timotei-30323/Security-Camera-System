namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Built-in admin bootstrap settings.
/// Bound from <c>Admin</c> (env var: <c>Admin__DeviceCode</c>).
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public const string AdminUsername = "admin";

    /// <summary>Physical device code printed on the camera hardware; used as the admin's initial password.</summary>
    public string DeviceCode { get; set; } = string.Empty;
}
