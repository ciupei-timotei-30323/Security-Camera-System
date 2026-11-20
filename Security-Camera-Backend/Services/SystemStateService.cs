namespace Security_Camera_Backend.Services;

/// <summary>
/// Default placeholder implementation: the system is always armed.
/// Replace with a database-backed implementation without touching the ingestion service.
/// </summary>
public sealed class SystemStateService : ISystemStateService
{
    public Task<bool> IsArmedAsync(CancellationToken ct = default) => Task.FromResult(true);
}
