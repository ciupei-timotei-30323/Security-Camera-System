using System.Security.Claims;
using Security_Camera_Backend.Exceptions;
using Security_Camera_Backend.Services.Auth;

namespace Security_Camera_Backend.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static string? GetSystemId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(AuthClaimTypes.SystemId);

    /// <summary>
    /// Returns the tenant id from the JWT. Tokens issued by this API always carry it; a missing
    /// claim means the token was forged or was issued before multi-tenancy and must not be trusted.
    /// </summary>
    public static string GetRequiredSystemId(this ClaimsPrincipal principal)
    {
        var systemId = principal.GetSystemId();

        if (string.IsNullOrWhiteSpace(systemId))
        {
            throw new UnauthorizedAppException(
                "Token does not identify a camera system.",
                "Authenticated principal without a system_id claim.");
        }

        return systemId;
    }

    public static string? GetUsername(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(AuthClaimTypes.Name) ?? principal.Identity?.Name;

    public static string GetRequiredUsername(this ClaimsPrincipal principal)
    {
        var username = principal.GetUsername();

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new UnauthorizedAppException(
                "Token does not identify a user.",
                "Authenticated principal without a name claim.");
        }

        return username;
    }

    public static bool IsExternalAdmin(this ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirstValue(AuthClaimTypes.ExternalAdmin),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
