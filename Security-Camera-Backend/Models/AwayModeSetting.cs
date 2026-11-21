namespace Security_Camera_Backend.Models;

/// <summary>
/// Per-system away-mode schedule. Exactly one row exists per <see cref="SystemId"/> and is created
/// lazily with <see cref="Mode"/> set to <see cref="AwayMode.Indefinite"/>, preserving the
/// historic "armed by default" behaviour.
/// </summary>
public class AwayModeSetting
{
    public string SystemId { get; set; } = string.Empty;

    public AwayMode Mode { get; set; } = AwayMode.Indefinite;

    /// <summary>Local start time of day for <see cref="AwayMode.Scheduled"/>.</summary>
    public TimeSpan? StartTime { get; set; }

    /// <summary>Local end time of day for <see cref="AwayMode.Scheduled"/>; may be before the start (overnight).</summary>
    public TimeSpan? EndTime { get; set; }

    public string? AlertEmail { get; set; }

    /// <summary>IANA time-zone id used to evaluate the schedule.</summary>
    public string TimeZone { get; set; } = AwayModeValues.DefaultTimeZone;

    /// <summary>
    /// Legacy flag kept for backwards compatibility. Always derived from <see cref="Mode"/>;
    /// <c>true</c> whenever the system is not explicitly disarmed.
    /// </summary>
    public bool IsArmed { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; }
}
