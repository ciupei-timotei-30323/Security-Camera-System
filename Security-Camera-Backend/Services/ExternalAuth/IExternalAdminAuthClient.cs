namespace Security_Camera_Backend.Services.ExternalAuth;

/// <summary>Result returned by the external factory IAM service for a valid administrator.</summary>
public sealed record ExternalAdminValidationResult(string SystemId, string Username, string? PiApiKey);

/// <summary>
/// Validates factory-level administrator credentials. Sub-users are validated locally; only
/// administrators are backed by the external factory service.
/// </summary>
public interface IExternalAdminAuthClient
{
    /// <summary>
    /// Returns the administrator's camera system on success, or <c>null</c> when the external
    /// service rejects the credentials (HTTP 401/404).
    /// </summary>
    Task<ExternalAdminValidationResult?> ValidateAdminAsync(
        string username,
        string password,
        CancellationToken ct);
}
