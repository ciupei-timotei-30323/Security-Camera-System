using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

public sealed record JwtToken(string Token, DateTime ExpiresAtUtc);

public interface IJwtTokenService
{
    /// <summary>Issues a token for a locally stored sub-user or the built-in admin.</summary>
    JwtToken CreateToken(User user);

    /// <summary>
    /// Issues a token for an identity that is not necessarily stored locally (external factory
    /// administrators) or for a local user when the caller already has the claims at hand.
    /// </summary>
    JwtToken CreateToken(
        string subject,
        string username,
        string role,
        string systemId,
        bool externalAdmin = false);
}
