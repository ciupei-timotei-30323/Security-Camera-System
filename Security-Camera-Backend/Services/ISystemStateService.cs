namespace Security_Camera_Backend.Services;

/// <summary>
/// Provides the arm/disarm state of a camera system at a specific instant, backed by
/// <see cref="Models.AwayModeSetting"/>.
/// </summary>
public interface ISystemStateService
{
    /// <summary>
    /// Evaluates the away-mode schedule at <paramref name="atUtc"/>. Callers evaluating a
    /// detection must pass the event timestamp, not the server's current time.
    /// </summary>
    Task<bool> IsArmedAsync(string systemId, DateTimeOffset atUtc, CancellationToken ct = default);

    /// <summary>Evaluates the away-mode schedule at the current UTC instant.</summary>
    Task<bool> IsArmedAsync(string systemId, CancellationToken ct = default) =>
        IsArmedAsync(systemId, DateTimeOffset.UtcNow, ct);
}
