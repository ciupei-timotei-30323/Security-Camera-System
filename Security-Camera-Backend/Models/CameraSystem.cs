namespace Security_Camera_Backend.Models;

/// <summary>
/// A camera system (one physical kit / factory site). All dashboard data belongs to exactly one
/// system, so every tenant-scoped query filters on <see cref="SystemId"/>.
/// </summary>
public class CameraSystem
{
    /// <summary>System used by the legacy single-Pi deployment.</summary>
    public const string DefaultSystemId = "sys-001";

    public string SystemId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<PiDevice> PiDevices { get; set; } = new List<PiDevice>();
}
