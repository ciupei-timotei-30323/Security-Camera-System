using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Auth;

public sealed class JwtTokenService(IOptions<JwtOptions> jwtOptions) : IJwtTokenService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public JwtToken CreateToken(User user) =>
        CreateToken(
            user.Id.ToString(CultureInfo.InvariantCulture),
            user.Username,
            user.Role,
            user.SystemId);

    public JwtToken CreateToken(
        string subject,
        string username,
        string role,
        string systemId,
        bool externalAdmin = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        // Whole seconds keep `exp`/`nbf` and the API response free of sub-second noise.
        var issuedAt = TruncateToSeconds(DateTime.UtcNow);
        var expiresAt = issuedAt.AddMinutes(Math.Max(1, _jwt.ExpirationMinutes));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(AuthClaimTypes.Name, username),
            new(AuthClaimTypes.Role, role),
            new(AuthClaimTypes.SystemId, systemId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        if (externalAdmin)
        {
            claims.Add(new Claim(AuthClaimTypes.ExternalAdmin, "true"));
        }

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expiresAt,
            signingCredentials: signingCredentials);

        return new JwtToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}
