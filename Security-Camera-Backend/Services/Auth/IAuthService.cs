using System.Security.Claims;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Exceptions;

namespace Security_Camera_Backend.Services.Auth;

public interface IAuthService
{
    /// <summary>
    /// Validates factory-admin credentials first, then local sub-user credentials. Throws
    /// <see cref="ValidationAppException"/> for missing/ambiguous input and
    /// <see cref="UnauthorizedAppException"/> for invalid credentials.
    /// </summary>
    Task<LoginResponse> LoginAsync(
        string? username,
        string? password,
        string? systemId,
        CancellationToken ct);

    /// <summary>
    /// Revokes the presented token's <c>jti</c> until its <c>exp</c> so it can no longer authenticate.
    /// </summary>
    Task LogoutAsync(ClaimsPrincipal principal, CancellationToken ct);

    /// <summary>
    /// Returns the authenticated user's profile. Local users are re-read from the database so
    /// deleted accounts are rejected immediately; external admins are claim-backed.
    /// </summary>
    Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken ct);
}
