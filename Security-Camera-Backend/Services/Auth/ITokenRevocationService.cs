namespace Security_Camera_Backend.Services.Auth;

/// <summary>Server-side JWT deny-list backed by the <c>RevokedTokens</c> table.</summary>
public interface ITokenRevocationService
{
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct);

    /// <summary>
    /// Deny-lists <paramref name="jti"/> until <paramref name="expiresAtUtc"/> and prunes already expired
    /// rows. Repeated calls for the same <paramref name="jti"/> are no-ops.
    /// </summary>
    Task RevokeAsync(string jti, DateTime expiresAtUtc, CancellationToken ct);
}
