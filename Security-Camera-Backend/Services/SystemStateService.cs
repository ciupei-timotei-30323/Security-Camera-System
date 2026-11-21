using Security_Camera_Backend.Services.Settings;

namespace Security_Camera_Backend.Services;

/// <summary>
/// Database-backed away-mode state. A missing <see cref="Models.AwayModeSetting"/> row is
/// created lazily as indefinite-armed, so newly registered systems default to the historic
/// behaviour.
/// </summary>
public sealed class SystemStateService(IAwayModeSettingsService awayModeSettingsService)
    : ISystemStateService
{
    public async Task<bool> IsArmedAsync(
        string systemId,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        var setting = await awayModeSettingsService.GetOrCreateEntityAsync(systemId, ct);

        return AwayModeSchedule.IsArmed(setting, atUtc);
    }
}
