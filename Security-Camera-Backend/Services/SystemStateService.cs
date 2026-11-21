using Security_Camera_Backend.Services.Settings;

namespace Security_Camera_Backend.Services;

/// <summary>
/// Database-backed arm/disarm state. A missing <see cref="Models.AwayModeSetting"/> row is
/// created lazily as armed, so newly registered systems default to the historic behaviour.
/// </summary>
public sealed class SystemStateService(IAwayModeSettingsService awayModeSettingsService)
    : ISystemStateService
{
    public async Task<bool> IsArmedAsync(string systemId, CancellationToken ct = default)
    {
        var setting = await awayModeSettingsService.GetOrCreateAsync(systemId, ct);

        return setting.IsArmed;
    }
}
