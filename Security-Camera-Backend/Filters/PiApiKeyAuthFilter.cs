using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Security;

namespace Security_Camera_Backend.Filters;

/// <summary>
/// Authenticates Raspberry Pi requests via the <c>X-Api-Key</c> header and resolves the camera
/// system that owns the key. Keys can come from <c>PiAuth:ApiKey</c> (legacy, mapped to
/// <c>PiAuth:DefaultSystemId</c>), <c>PiAuth:Systems</c>, or the <c>PiDevices</c> table populated
/// by external-admin logins.
/// </summary>
public sealed class PiApiKeyAuthFilter : IAsyncActionFilter
{
    public const string HeaderName = "X-Api-Key";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary><see cref="HttpContext.Items"/> key holding the resolved system id.</summary>
    public const string SystemIdItemKey = "PiSystemId";

    private readonly ILogger<PiApiKeyAuthFilter> _logger;
    private readonly SecurityCameraBackendContext _dbContext;
    private readonly List<(byte[] KeyHash, string SystemId)> _configuredKeys = [];

    public PiApiKeyAuthFilter(
        IOptions<PiAuthOptions> options,
        SecurityCameraBackendContext dbContext,
        ILogger<PiApiKeyAuthFilter> logger)
    {
        _logger = logger;
        _dbContext = dbContext;

        var piAuth = options.Value;

        // Backwards compatibility: the pre-multi-tenant single key keeps working as sys-001.
        if (!string.IsNullOrWhiteSpace(piAuth.ApiKey))
        {
            AddConfiguredKey(
                piAuth.ApiKey,
                string.IsNullOrWhiteSpace(piAuth.DefaultSystemId)
                    ? PiAuthOptions.DefaultSystemIdValue
                    : piAuth.DefaultSystemId);
        }

        foreach (var (systemId, apiKey) in piAuth.Systems)
        {
            if (string.IsNullOrWhiteSpace(systemId) || string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            AddConfiguredKey(apiKey, systemId);
        }
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedKey) ||
            string.IsNullOrWhiteSpace(providedKey))
        {
            _logger.LogWarning(
                "Rejected {Method} {Path}: missing {Header} header.",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, HeaderName);
            ShortCircuit(context);
            return;
        }

        var systemId = ResolveConfiguredSystem(providedKey.ToString())
                       ?? await ResolveDatabaseSystemAsync(providedKey.ToString(), context.HttpContext.RequestAborted);

        if (string.IsNullOrWhiteSpace(systemId))
        {
            _logger.LogWarning(
                "Rejected {Method} {Path}: unknown {Header}.",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, HeaderName);
            ShortCircuit(context);
            return;
        }

        context.HttpContext.Items[SystemIdItemKey] = systemId;

        await TouchDeviceAsync(providedKey.ToString(), context.HttpContext.RequestAborted);

        _logger.LogDebug(
            "Resolved camera system {SystemId} for Pi request {Method} {Path}.",
            systemId, context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        await next();
    }

    private void AddConfiguredKey(string apiKey, string systemId) =>
        _configuredKeys.Add((PiApiKeyHasher.Hash(apiKey), systemId));

    private string? ResolveConfiguredSystem(string providedKey)
    {
        if (_configuredKeys.Count == 0)
        {
            return null;
        }

        var providedHash = PiApiKeyHasher.Hash(providedKey);
        string? matchedSystemId = null;

        // Compare against every configured key so the loop duration does not reveal which
        // (if any) key matched.
        foreach (var (keyHash, systemId) in _configuredKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(providedHash, keyHash))
            {
                matchedSystemId = systemId;
            }
        }

        return matchedSystemId;
    }

    private async Task<string?> ResolveDatabaseSystemAsync(string providedKey, CancellationToken ct)
    {
        var apiKeyHash = PiApiKeyHasher.HashToHex(providedKey);

        return await _dbContext.PiDevices
            .AsNoTracking()
            .Where(device => device.ApiKeyHash == apiKeyHash)
            .Select(device => device.SystemId)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Refreshes <see cref="PiDevice.LastSeenAt"/> for the authenticated key. The conditional
    /// UPDATE makes the 5-second throttle atomic across concurrent requests and app instances.
    /// </summary>
    private async Task TouchDeviceAsync(string providedKey, CancellationToken ct)
    {
        var apiKeyHash = PiApiKeyHasher.HashToHex(providedKey);
        var now = DateTime.UtcNow;
        var cutoff = now - HeartbeatInterval;

        await _dbContext.PiDevices
            .Where(device => device.ApiKeyHash == apiKeyHash &&
                             (device.LastSeenAt == null || device.LastSeenAt < cutoff))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(device => device.LastSeenAt, now),
                ct);
    }

    private static void ShortCircuit(ActionExecutingContext context) =>
        context.Result = new UnauthorizedObjectResult(new { message = "Invalid or missing API key." });
}
