namespace Security_Camera_Backend.Services;

/// <summary>
/// 16x2 LCD text formatting shared by the HTTP status endpoint and the MQTT alarm payload.
/// Keeping one implementation guarantees the ESP32 sees exactly the same strings whether it polls
/// or subscribes.
/// </summary>
public static class TerminalDisplay
{
    public const int Width = 16;

    /// <summary>Truncates to <see cref="Width"/> characters or pads with spaces.</summary>
    public static string Line(string? value)
    {
        var text = value ?? string.Empty;

        return text.Length >= Width
            ? text[..Width]
            : text.PadRight(Width);
    }

    public static string BlankLine() => new(' ', Width);
}
