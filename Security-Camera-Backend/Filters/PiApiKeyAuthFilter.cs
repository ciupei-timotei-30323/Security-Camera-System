using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Filters;

/// <summary>
/// Authenticates Raspberry Pi requests via the <c>X-Api-Key</c> header.
/// Applied per-endpoint (via <see cref="ServiceFilterAttribute"/>) so it cannot interfere with
/// future dashboard/user authentication schemes.
/// </summary>
public sealed class PiApiKeyAuthFilter : IAsyncActionFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly ILogger<PiApiKeyAuthFilter> _logger;
    private readonly byte[] _expectedKeyHash;
    private readonly bool _isConfigured;

    public PiApiKeyAuthFilter(IOptions<PiAuthOptions> options, ILogger<PiApiKeyAuthFilter> logger)
    {
        _logger = logger;

        var apiKey = options.Value.ApiKey;
        _isConfigured = !string.IsNullOrWhiteSpace(apiKey);
        _expectedKeyHash = Hash(apiKey ?? string.Empty);
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!_isConfigured)
        {
            _logger.LogError("PiAuth:ApiKey is not configured; rejecting all ingestion requests.");
            ShortCircuit(context);
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedKey) ||
            string.IsNullOrWhiteSpace(providedKey))
        {
            _logger.LogWarning("Rejected {Method} {Path}: missing {Header} header.",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, HeaderName);
            ShortCircuit(context);
            return;
        }

        // Hash both sides so the comparison is constant-time regardless of the supplied key length.
        var providedKeyHash = Hash(providedKey.ToString());

        if (!CryptographicOperations.FixedTimeEquals(providedKeyHash, _expectedKeyHash))
        {
            _logger.LogWarning("Rejected {Method} {Path}: invalid {Header} header.",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, HeaderName);
            ShortCircuit(context);
            return;
        }

        await next();
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static void ShortCircuit(ActionExecutingContext context) =>
        context.Result = new UnauthorizedObjectResult(new { message = "Invalid or missing API key." });
}
