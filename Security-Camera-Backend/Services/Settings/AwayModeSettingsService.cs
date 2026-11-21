using Microsoft.EntityFrameworkCore;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Settings;

public sealed class AwayModeSettingsService(
    SecurityCameraBackendContext dbContext,
    ILogger<AwayModeSettingsService> logger) : IAwayModeSettingsService
{
    public async Task<AwayModeSettingResponse> GetOrCreateAsync(string systemId, CancellationToken ct)
    {
        var setting = await dbContext.AwayModeSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.SystemId == systemId, ct);

        if (setting is not null)
        {
            return Map(setting);
        }

        setting = new AwayModeSetting
        {
            SystemId = systemId,
            IsArmed = true,
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
                .AsNoTracking()
                .SingleAsync(s => s.SystemId == systemId, ct);
        }

        return Map(setting);
    }

    public async Task<AwayModeSettingResponse> UpdateAsync(string systemId, bool isArmed, CancellationToken ct)
    {
        var setting = await dbContext.AwayModeSettings
            .SingleOrDefaultAsync(s => s.SystemId == systemId, ct);

        if (setting is null)
        {
            setting = new AwayModeSetting
            {
                SystemId = systemId,
                IsArmed = isArmed,
                UpdatedAtUtc = DateTime.UtcNow
            };
            dbContext.AwayModeSettings.Add(setting);
        }
        else
        {
            setting.IsArmed = isArmed;
            setting.UpdatedAtUtc = DateTime.UtcNow;
        }

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

            existing.IsArmed = isArmed;
            existing.UpdatedAtUtc = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(ct);
            setting = existing;
        }

        logger.LogInformation(
            "System {SystemId} away mode set to {IsArmed}.",
            systemId, setting.IsArmed);

        return Map(setting);
    }

    private static AwayModeSettingResponse Map(AwayModeSetting setting) =>
        new(setting.SystemId, setting.IsArmed, setting.UpdatedAtUtc);
}
