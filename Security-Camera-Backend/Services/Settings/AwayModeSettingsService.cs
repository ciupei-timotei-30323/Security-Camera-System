using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Settings;

public sealed class AwayModeSettingsService(
    SecurityCameraBackendContext dbContext,
    ILogger<AwayModeSettingsService> logger) : IAwayModeSettingsService
{
    public async Task<AwayModeSettingResponse> GetOrCreateAsync(string systemId, CancellationToken ct)
    {
        var setting = await GetOrCreateEntityAsync(systemId, ct);

        return Map(setting, DateTimeOffset.UtcNow);
    }

    public async Task<AwayModeSetting> GetOrCreateEntityAsync(string systemId, CancellationToken ct)
    {
        var setting = await dbContext.AwayModeSettings
            .SingleOrDefaultAsync(s => s.SystemId == systemId, ct);

        if (setting is not null)
        {
            return setting;
        }

        setting = new AwayModeSetting
        {
            SystemId = systemId,
            Mode = AwayMode.Indefinite,
            IsArmed = true,
            TimeZone = AwayModeValues.DefaultTimeZone,
            UpdatedAtUtc = DateTime.UtcNow
        };

        dbContext.AwayModeSettings.Add(setting);

        try
        {
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Initialized away-mode settings for system {SystemId}.", systemId);
        }
        catch (DbUpdateException ex)
        {
            // Another request initialized the same system concurrently; return its row.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(ex, "Concurrent away-mode initialization for system {SystemId}.", systemId);

            setting = await dbContext.AwayModeSettings
                .SingleAsync(s => s.SystemId == systemId, ct);
        }

        return setting;
    }

    public async Task<AwayModeSettingResponse> UpdateAsync(
        string systemId,
        UpdateAwayModeSettingRequest request,
        CancellationToken ct)
    {
        var values = Validate(request);

        var setting = await dbContext.AwayModeSettings
            .SingleOrDefaultAsync(s => s.SystemId == systemId, ct);

        if (setting is null)
        {
            setting = new AwayModeSetting { SystemId = systemId };
            dbContext.AwayModeSettings.Add(setting);
        }

        setting.Mode = values.Mode;
        setting.StartTime = values.StartTime;
        setting.EndTime = values.EndTime;
        setting.AlertEmail = values.AlertEmail;
        setting.TimeZone = values.TimeZone;
        setting.IsArmed = values.Mode != AwayMode.Off;
        setting.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Concurrent insert for a brand-new system: fall back to an update of the existing row.
            dbContext.ChangeTracker.Clear();
            logger.LogDebug(ex, "Concurrent away-mode update for system {SystemId}; retrying.", systemId);

            var existing = await dbContext.AwayModeSettings
                .SingleAsync(s => s.SystemId == systemId, ct);

            existing.Mode = values.Mode;
            existing.StartTime = values.StartTime;
            existing.EndTime = values.EndTime;
            existing.AlertEmail = values.AlertEmail;
            existing.TimeZone = values.TimeZone;
            existing.IsArmed = values.Mode != AwayMode.Off;
            existing.UpdatedAtUtc = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(ct);
            setting = existing;
        }

        logger.LogInformation(
            "System {SystemId} away mode set to {Mode} (time zone {TimeZone}).",
            systemId, setting.Mode, setting.TimeZone);

        return Map(setting, DateTimeOffset.UtcNow);
    }

    private static ValidatedAwayMode Validate(UpdateAwayModeSettingRequest request)
    {
        AwayMode mode;

        if (request.Mode is null)
        {
            // Legacy shape: { "isArmed": true|false }.
            if (request.IsArmed is null)
            {
                throw new ValidationAppException(
                    "Either 'mode' or the legacy 'isArmed' field is required.",
                    "Away-mode update without mode or isArmed.");
            }

            mode = request.IsArmed.Value ? AwayMode.Indefinite : AwayMode.Off;
        }
        else if (!AwayModeValues.TryParse(request.Mode, out mode))
        {
            throw new ValidationAppException(
                "Mode must be 'off', 'indefinite' or 'scheduled'.",
                $"Away-mode update used unsupported mode '{request.Mode}'.");
        }

        var timeZoneId = string.IsNullOrWhiteSpace(request.TimeZone)
            ? AwayModeValues.DefaultTimeZone
            : request.TimeZone.Trim();

        try
        {
            AwayModeSchedule.ResolveTimeZone(timeZoneId);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationAppException(
                "TimeZone must be a valid IANA time-zone id, for example 'Europe/Bucharest'.",
                ex.Message);
        }

        var alertEmail = string.IsNullOrWhiteSpace(request.AlertEmail)
            ? null
            : request.AlertEmail.Trim();

        if (alertEmail is not null && !IsValidEmail(alertEmail))
        {
            throw new ValidationAppException(
                "AlertEmail must be a valid e-mail address.",
                $"Away-mode update used invalid alert e-mail '{alertEmail}'.");
        }

        if (mode != AwayMode.Scheduled)
        {
            return new ValidatedAwayMode(mode, null, null, alertEmail, timeZoneId);
        }

        if (!AwayModeSchedule.TryParseTimeOfDay(request.StartTime, out var start) ||
            !AwayModeSchedule.TryParseTimeOfDay(request.EndTime, out var end))
        {
            throw new ValidationAppException(
                "Scheduled mode requires StartTime and EndTime in HH:mm format.",
                $"Away-mode update used start '{request.StartTime}' and end '{request.EndTime}'.");
        }

        return new ValidatedAwayMode(mode, start, end, alertEmail, timeZoneId);
    }

    private static bool IsValidEmail(string value)
    {
        try
        {
            var address = new MailAddress(value);

            return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AwayModeSettingResponse Map(AwayModeSetting setting, DateTimeOffset nowUtc) => new(
        setting.SystemId,
        AwayModeValues.ToWireValue(setting.Mode),
        setting.StartTime is null ? null : AwayModeSchedule.FormatTimeOfDay(setting.StartTime.Value),
        setting.EndTime is null ? null : AwayModeSchedule.FormatTimeOfDay(setting.EndTime.Value),
        setting.AlertEmail,
        setting.TimeZone,
        AwayModeSchedule.IsArmed(setting, nowUtc),
        setting.Mode != AwayMode.Off,
        setting.UpdatedAtUtc);

    private sealed record ValidatedAwayMode(
        AwayMode Mode,
        TimeSpan? StartTime,
        TimeSpan? EndTime,
        string? AlertEmail,
        string TimeZone);
}
