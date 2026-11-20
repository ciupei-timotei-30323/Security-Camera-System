using System.Security.Claims;
using Security_Camera_Backend.Contracts;

namespace Security_Camera_Backend.Services.Auth;

public interface IAuthService
{
    /// <summary>
    /// Validates credentials and issues a JWT. Throws <see cref="Exceptions.ValidationAppException"/>
    /// for missing input and <see cref="Exceptions.UnauthorizedAppException"/> for invalid credentials.
    /// </summary>
    Task<LoginResponse> LoginAsync(string? username, string? password, CancellationToken ct);

    /// <summary>
    /// Revokes the presented token's <c>jti</c> until its <c>exp</c> so it can no longer authenticate.
    /// </summary>
    Task LogoutAsync(ClaimsPrincipal principal, CancellationToken ct);

    /// <summary>
    /// Returns the authenticated user's profile, loaded from the database so deleted accounts are
    /// rejected immediately even while their JWT is still valid.
    /// </summary>
    Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken ct);
}
