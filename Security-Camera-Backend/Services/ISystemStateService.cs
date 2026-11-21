namespace Security_Camera_Backend.Services;

/// <summary>
/// Provides the current arm/disarm state of a camera system, backed by
/// <see cref="Models.AwayModeSetting"/>.
/// </summary>
public interface ISystemStateService
{
    Task<bool> IsArmedAsync(string systemId, CancellationToken ct = default);
}
