using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Settings;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

public sealed class AwayModeScheduleTests
{
    [Fact]
    public void Scheduled_IsArmedExactlyAtStart_AndDisarmedExactlyAtEnd()
    {
        var setting = Scheduled("08:00", "17:00", "UTC");

        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 8, 0)));
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 8, 1)));
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 16, 59)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 17, 0)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 7, 59)));
    }

    [Fact]
    public void Scheduled_CrossingMidnight_IsArmedOnBothSides()
    {
        var setting = Scheduled("22:00", "06:00", "UTC");

        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 22, 0)));
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 23, 30)));
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 16, 0, 0)));
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 16, 5, 59)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 16, 6, 0)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 16, 12, 0)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 16, 21, 59)));
    }

    [Fact]
    public void Off_IsNeverArmed_AndIndefinite_IsAlwaysArmed()
    {
        Assert.False(AwayModeSchedule.IsArmed(
            new AwayModeSetting { Mode = AwayMode.Off, TimeZone = "UTC" },
            Utc(2026, 1, 15, 12, 0)));

        Assert.True(AwayModeSchedule.IsArmed(
            new AwayModeSetting { Mode = AwayMode.Indefinite, TimeZone = "UTC" },
            Utc(2026, 1, 15, 12, 0)));
    }

    [Fact]
    public void Scheduled_RespectsTimeZoneAndDstSpringForward()
    {
        // Europe/Bucharest jumps from 03:00 EET to 04:00 EEST at 01:00 UTC on 2025-03-30.
        var setting = Scheduled("03:30", "04:30", "Europe/Bucharest");

        // 00:45 UTC is 02:45 local (before the window).
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2025, 3, 30, 0, 45)));

        // 01:15 UTC is 04:15 local (inside the window after the jump).
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2025, 3, 30, 1, 15)));

        // 01:30 UTC is 04:30 local (exactly at the end).
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2025, 3, 30, 1, 30)));
    }

    [Fact]
    public void Scheduled_RespectsDstFallBack()
    {
        // Europe/Bucharest falls back from 04:00 EEST to 03:00 EET at 01:00 UTC on 2025-10-26.
        var setting = Scheduled("02:30", "03:30", "Europe/Bucharest");

        // 00:30 UTC is 03:30 EEST (exactly at the end, before the fallback).
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2025, 10, 26, 0, 30)));

        // 01:00 UTC is 03:00 EET (inside the window again after the fallback).
        Assert.True(AwayModeSchedule.IsArmed(setting, Utc(2025, 10, 26, 1, 0)));

        // 01:30 UTC is 03:30 EET (exactly at the end).
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2025, 10, 26, 1, 30)));
    }

    [Fact]
    public void ZeroLengthWindow_IsNeverArmed()
    {
        var setting = Scheduled("09:00", "09:00", "UTC");

        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 9, 0)));
        Assert.False(AwayModeSchedule.IsArmed(setting, Utc(2026, 1, 15, 12, 0)));
    }

    private static AwayModeSetting Scheduled(string start, string end, string timeZone)
    {
        Assert.True(AwayModeSchedule.TryParseTimeOfDay(start, out var startTime));
        Assert.True(AwayModeSchedule.TryParseTimeOfDay(end, out var endTime));

        return new AwayModeSetting
        {
            Mode = AwayMode.Scheduled,
            StartTime = startTime,
            EndTime = endTime,
            TimeZone = timeZone
        };
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
