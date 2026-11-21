namespace Security_Camera_Backend.Contracts;

/// <summary>Tiny polling payload for the ESP32 alert terminal's 16x2 LCD and buzzer.</summary>
public sealed record DeviceTerminalStatusResponse(
    bool Armed,
    bool Alarm,
    DateTimeOffset? AlarmUntil,
    string Line1,
    string Line2,
    DateTimeOffset ServerTime);
