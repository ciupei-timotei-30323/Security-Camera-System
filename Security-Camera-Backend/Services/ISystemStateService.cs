namespace Security_Camera_Backend.Services;

/// <summary>
/// Provides the current arm/disarm state of the security system.
/// Will later be backed by persisted database settings.
/// </summary>
public interface ISystemStateService
{
    Task<bool> IsArmedAsync(CancellationToken ct = default);
}
