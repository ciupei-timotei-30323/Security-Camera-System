namespace Security_Camera_Backend.Models;

/// <summary>
/// A Raspberry Pi camera kit bound to a system. Only the SHA-256 hash of the API key is stored,
/// so a database leak does not expose usable credentials.
/// </summary>
public class PiDevice
{
    public int Id { get; set; }

    public string SystemId { get; set; } = string.Empty;

    /// <summary>Uppercase hexadecimal SHA-256 of the raw <c>X-Api-Key</c> value.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    public string? Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public CameraSystem? System { get; set; }
}
