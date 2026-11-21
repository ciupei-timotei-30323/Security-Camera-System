namespace Security_Camera_Backend.Models;

/// <summary>
/// Per-system arm/disarm state. Exactly one row exists per <see cref="SystemId"/> and is created
/// lazily with <see cref="IsArmed"/> set to <c>true</c>.
/// </summary>
public class AwayModeSetting
{
    public string SystemId { get; set; } = string.Empty;

    public bool IsArmed { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; }
}
