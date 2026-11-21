namespace Security_Camera_Backend.Services.Cv;

/// <summary>Returns the cached <c>ok</c>/<c>down</c> health of the internal cv-service.</summary>
public interface ICvServiceHealthService
{
    Task<string> GetStatusAsync(CancellationToken ct);
}
