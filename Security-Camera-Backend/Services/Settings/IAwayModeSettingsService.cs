using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services.Settings;

/// <summary>
/// Reads and updates the per-system away-mode setting. Missing rows are created lazily with
/// <c>IsArmed = true</c>, preserving the historical "always armed" behaviour.
/// </summary>
public interface IAwayModeSettingsService
{
    Task<AwayModeSettingResponse> GetOrCreateAsync(string systemId, CancellationToken ct);

    Task<AwayModeSettingResponse> UpdateAsync(string systemId, bool isArmed, CancellationToken ct);
}
