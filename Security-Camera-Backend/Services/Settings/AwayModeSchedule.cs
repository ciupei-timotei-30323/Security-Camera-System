using System.Globalization;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Settings;

/// <summary>
/// Pure schedule evaluation. All schedule boundaries are local wall-clock times in the
/// configured IANA time zone. The range is half-open: <c>[start, end)</c>, so the system is
/// armed exactly at <c>start</c> and disarmed exactly at <c>end</c>. When <c>end</c> is before
/// <c>start</c> the range wraps over midnight.
/// </summary>
public static class AwayModeSchedule
{
    public static bool IsArmed(AwayModeSetting setting, DateTimeOffset atUtc)
    {
        switch (setting.Mode)
        {
            case AwayMode.Off:
                return false;
            case AwayMode.Indefinite:
                return true;
            case AwayMode.Scheduled:
                return IsWithinSchedule(setting, atUtc);
            default:
                return false;
        }
    }

    public static bool IsWithinSchedule(AwayModeSetting setting, DateTimeOffset atUtc)
    {
        if (setting.StartTime is null || setting.EndTime is null)
        {
            return false;
        }

        var timeZone = ResolveTimeZone(setting.TimeZone);
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(atUtc.UtcDateTime, timeZone).TimeOfDay;

        var start = setting.StartTime.Value;
        var end = setting.EndTime.Value;

        if (start == end)
        {
            // A zero-length window never arms; use "off"/"indefinite" for those semantics.
            return false;
        }

        return start < end
            ? localTime >= start && localTime < end
            : localTime >= start || localTime < end;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var id = string.IsNullOrWhiteSpace(timeZoneId)
            ? AwayModeValues.DefaultTimeZone
            : timeZoneId.Trim();

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Unknown IANA time zone '{id}'.", nameof(timeZoneId), ex);
        }
    }

    public static bool TryParseTimeOfDay(string? value, out TimeSpan timeOfDay) =>
        TimeSpan.TryParseExact(
            value?.Trim(),
            @"hh\:mm",
            CultureInfo.InvariantCulture,
            out timeOfDay);

    public static string FormatTimeOfDay(TimeSpan timeOfDay) =>
        $"{timeOfDay.Hours:D2}:{timeOfDay.Minutes:D2}";
}
