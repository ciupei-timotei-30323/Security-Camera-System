using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Settings;

/// <summary>
/// Reads and updates the per-system away-mode setting. Missing rows are created lazily as
/// indefinite-armed, preserving the historical "always armed" behaviour.
/// </summary>
public interface IAwayModeSettingsService
{
    Task<AwayModeSettingResponse> GetOrCreateAsync(string systemId, CancellationToken ct);

    /// <summary>Returns the tracked entity used to evaluate schedules against event timestamps.</summary>
    Task<AwayModeSetting> GetOrCreateEntityAsync(string systemId, CancellationToken ct);

    Task<AwayModeSettingResponse> UpdateAsync(
        string systemId,
        UpdateAwayModeSettingRequest request,
        CancellationToken ct);
}
