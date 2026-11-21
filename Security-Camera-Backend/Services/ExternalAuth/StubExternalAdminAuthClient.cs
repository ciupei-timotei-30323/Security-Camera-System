using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Services.ExternalAuth;

/// <summary>
/// Development/test implementation of <see cref="IExternalAdminAuthClient"/> backed by
/// <c>ExternalAuth:StubAccounts</c>. It behaves like the future factory service: unknown users
/// and wrong passwords both return <c>null</c> (a 401 from the real service).
/// </summary>
public sealed class StubExternalAdminAuthClient(
    IOptions<ExternalAuthOptions> options,
    ILogger<StubExternalAdminAuthClient> logger) : IExternalAdminAuthClient
{
    public Task<ExternalAdminValidationResult?> ValidateAdminAsync(
        string username,
        string password,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var account = options.Value.StubAccounts
            .FirstOrDefault(pair => string.Equals(pair.Key, username, StringComparison.OrdinalIgnoreCase));

        if (account.Value is null || !PiApiKeyHasher.FixedTimeEquals(account.Value.Password, password))
        {
            logger.LogWarning("Stub external auth rejected administrator {Username}.", username);
            return Task.FromResult<ExternalAdminValidationResult?>(null);
        }

        if (string.IsNullOrWhiteSpace(account.Value.SystemId))
        {
            logger.LogError(
                "Stub account {Username} has no SystemId configured; rejecting login.",
                username);
            return Task.FromResult<ExternalAdminValidationResult?>(null);
        }

        logger.LogInformation(
            "Stub external auth accepted administrator {Username} for system {SystemId}.",
            account.Key, account.Value.SystemId);

        return Task.FromResult<ExternalAdminValidationResult?>(new ExternalAdminValidationResult(
            account.Value.SystemId,
            account.Key,
            account.Value.PiApiKey));
    }
}
