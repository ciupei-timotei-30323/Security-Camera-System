namespace Security_Camera_Backend.Models;

/// <summary>How a system decides whether a detection is a suspect event.</summary>
public enum AwayMode
{
    Off = 0,
    Indefinite = 1,
    Scheduled = 2
}

public static class AwayModeValues
{
    public const string Off = "off";
    public const string Indefinite = "indefinite";
    public const string Scheduled = "scheduled";

    public const string DefaultTimeZone = "Europe/Bucharest";

    public static bool TryParse(string? value, out AwayMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Off:
                mode = AwayMode.Off;
                return true;
            case Indefinite:
                mode = AwayMode.Indefinite;
                return true;
            case Scheduled:
                mode = AwayMode.Scheduled;
                return true;
            default:
                mode = AwayMode.Off;
                return false;
        }
    }

    public static string ToWireValue(AwayMode mode) => mode switch
    {
        AwayMode.Indefinite => Indefinite,
        AwayMode.Scheduled => Scheduled,
        _ => Off
    };
}
