using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Auth;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>Result of a MediaMTX authorization decision.</summary>
public sealed record MediaMtxAuthDecision(
    bool Allowed,
    DateTimeOffset? ExpireAt = null,
    string? Reason = null)
{
    public static MediaMtxAuthDecision Allow(DateTimeOffset? expireAt = null) => new(true, expireAt);

    public static MediaMtxAuthDecision Deny(string reason) => new(false, null, reason);
}

/// <summary>
/// Decides whether MediaMTX may <c>publish</c> or <c>read</c> a stream path.
///
/// Path convention: the path IS the <c>SystemId</c> (for example <c>sys-001</c>), so a single
/// rule enforces multi-tenancy for both actions:
/// <list type="bullet">
///   <item><b>publish</b> — the credential is a Raspberry Pi camera API key. It must belong to a
///   <see cref="DeviceType.Camera"/> device (alert-terminal keys are rejected) and its resolved
///   system must equal the path.</item>
///   <item><b>read</b> — the credential is a dashboard JWT. Signature, lifetime, issuer, audience
///   and the revoked-<c>jti</c> deny-list are all validated, and the <c>system_id</c> claim must
///   equal the path.</item>
/// </list>
/// </summary>
public sealed class MediaMtxPathAuthorizer(
    SecurityCameraBackendContext dbContext,
    IOptions<PiAuthOptions> piAuthOptions,
    IOptions<JwtOptions> jwtOptions,
    ITokenRevocationService tokenRevocationService,
    ILogger<MediaMtxPathAuthorizer> logger)
{
    private const string PublishAction = "publish";
    private const string ReadAction = "read";

    private readonly PiAuthOptions _piAuth = piAuthOptions.Value;
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<MediaMtxAuthDecision> AuthorizeAsync(
        string? action,
        string? path,
        string? password,
        string? token,
        string? query,
        CancellationToken ct)
    {
        var systemId = NormalizePath(path);

        if (systemId is null)
        {
            return MediaMtxAuthDecision.Deny("Missing or invalid stream path.");
        }

        var actionName = action?.Trim().ToLowerInvariant();

        return actionName switch
        {
            PublishAction => await AuthorizePublishAsync(systemId, password, token, query, ct),
            ReadAction => await AuthorizeReadAsync(systemId, token, query, ct),
            // MediaMTX also probes "api" and "metrics" actions; those are not stream access.
            _ => MediaMtxAuthDecision.Deny($"Unsupported action '{action}'."),
        };
    }

    private async Task<MediaMtxAuthDecision> AuthorizePublishAsync(
        string systemId,
        string? password,
        string? token,
        string? query,
        CancellationToken ct)
    {
        var apiKey = FirstNonEmpty(password, token, ExtractQueryToken(query));

        if (apiKey is null)
        {
            return MediaMtxAuthDecision.Deny("Missing camera API key.");
        }

        var hash = PiApiKeyHasher.HashToHex(apiKey);

        var device = await dbContext.PiDevices
            .AsNoTracking()
            .Where(d => d.ApiKeyHash == hash)
            .Select(d => new { d.SystemId, d.DeviceType })
            .SingleOrDefaultAsync(ct);

        if (device is not null)
        {
            if (device.DeviceType != DeviceType.Camera)
            {
                logger.LogWarning(
                    "Rejected MediaMTX publish for {SystemId}: device type {DeviceType} is not a camera.",
                    systemId, device.DeviceType);
                return MediaMtxAuthDecision.Deny("Only camera devices may publish streams.");
            }

            return string.Equals(device.SystemId, systemId, StringComparison.Ordinal)
                ? MediaMtxAuthDecision.Allow()
                : DenyCrossTenant(systemId, device.SystemId);
        }

        // Configured (environment) keys have no device row; they are always camera keys.
        var configuredSystemId = ResolveConfiguredKey(apiKey);

        if (configuredSystemId is null)
        {
            return MediaMtxAuthDecision.Deny("Unknown camera API key.");
        }

        return string.Equals(configuredSystemId, systemId, StringComparison.Ordinal)
            ? MediaMtxAuthDecision.Allow()
            : DenyCrossTenant(systemId, configuredSystemId);
    }

    private async Task<MediaMtxAuthDecision> AuthorizeReadAsync(
        string systemId,
        string? token,
        string? query,
        CancellationToken ct)
    {
        var jwt = FirstNonEmpty(token, ExtractQueryToken(query));

        if (jwt is null)
        {
            return MediaMtxAuthDecision.Deny("Missing stream token.");
        }

        var principal = ValidateToken(jwt);

        if (principal is null)
        {
            return MediaMtxAuthDecision.Deny("Invalid or expired stream token.");
        }

        var tokenSystemId = principal.FindFirstValue(AuthClaimTypes.SystemId);

        if (string.IsNullOrWhiteSpace(tokenSystemId))
        {
            return MediaMtxAuthDecision.Deny("Stream token has no system_id claim.");
        }

        if (!string.Equals(tokenSystemId, systemId, StringComparison.Ordinal))
        {
            return DenyCrossTenant(systemId, tokenSystemId);
        }

        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (!string.IsNullOrWhiteSpace(jti) && await tokenRevocationService.IsRevokedAsync(jti, ct))
        {
            return MediaMtxAuthDecision.Deny("Stream token has been revoked.");
        }

        var expireAt = ReadExpiry(principal);

        return MediaMtxAuthDecision.Allow(expireAt);
    }

    private ClaimsPrincipal? ValidateToken(string jwt)
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AuthClaimTypes.Name,
            RoleClaimType = AuthClaimTypes.Role,
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(jwt, validationParameters, out _);

            return principal;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger.LogDebug(ex, "Rejected MediaMTX read token: {Reason}", ex.Message);
            return null;
        }
    }

    private static DateTimeOffset? ReadExpiry(ClaimsPrincipal principal)
    {
        var exp = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);

        if (exp is null || !long.TryParse(exp, out var seconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private string? ResolveConfiguredKey(string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(_piAuth.ApiKey)
            && PiApiKeyHasher.FixedTimeEquals(_piAuth.ApiKey, apiKey))
        {
            return string.IsNullOrWhiteSpace(_piAuth.DefaultSystemId)
                ? PiAuthOptions.DefaultSystemIdValue
                : _piAuth.DefaultSystemId;
        }

        foreach (var (systemId, key) in _piAuth.Systems)
        {
            if (!string.IsNullOrWhiteSpace(key) && PiApiKeyHasher.FixedTimeEquals(key, apiKey))
            {
                return systemId;
            }
        }

        return null;
    }

    private MediaMtxAuthDecision DenyCrossTenant(string requestedSystemId, string credentialSystemId)
    {
        logger.LogWarning(
            "Rejected MediaMTX authorization for path {RequestedSystemId}: credential belongs to {CredentialSystemId}.",
            requestedSystemId, credentialSystemId);

        // MediaMTX only understands 200/401, so the response stays generic; the log keeps the
        // detail needed for leak investigations.
        return MediaMtxAuthDecision.Deny("Credential does not match the requested stream.");
    }

    /// <summary>
    /// MediaMTX sends the path without a leading slash, but a reverse proxy or manual probe may
    /// include one. Reject anything that is not a single well-formed segment so the path can never
    /// be smuggled into a different topic or tenant.
    /// </summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('/');

        if (trimmed.Length == 0 || trimmed.Length > 64)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
            {
                return null;
            }
        }

        return trimmed;
    }

    private static string? ExtractQueryToken(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var raw = query.Trim();

        if (raw.StartsWith('?'))
        {
            raw = raw[1..];
        }

        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator];

            if (!name.Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
