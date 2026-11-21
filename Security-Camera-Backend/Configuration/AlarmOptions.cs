namespace Security_Camera_Backend.Configuration;

/// <summary>Alert-terminal alarm timing. Bound from <c>Alarm</c>.</summary>
public sealed class AlarmOptions
{
    public const string SectionName = "Alarm";

    public int DurationSeconds { get; set; } = 15;
}
