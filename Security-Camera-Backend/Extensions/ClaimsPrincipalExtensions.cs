using System.IdentityModel.Tokens.Jwt;
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

    /// <summary>Parses the local user id from the <c>sub</c> claim; null for external admins.</summary>
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var rawUserId = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return int.TryParse(rawUserId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }

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
