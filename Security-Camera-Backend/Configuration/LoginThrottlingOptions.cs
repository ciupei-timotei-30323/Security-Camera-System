namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Brute-force protection for <c>POST /api/auth/login</c>.
/// Bound from <c>Auth:LoginRateLimit</c> (env vars: <c>Auth__LoginRateLimit__PermitLimit</c>,
/// <c>Auth__LoginRateLimit__WindowSeconds</c>, <c>Auth__LoginRateLimit__Enabled</c>).
/// </summary>
public sealed class LoginRateLimitOptions
{
    public const string SectionName = "Auth:LoginRateLimit";

    /// <summary>Maximum login requests allowed per client IP inside one window.</summary>
    public int PermitLimit { get; set; } = 5;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Escape hatch for load/performance testing only. Never disable this in production: it is the
    /// first line of defence against credential stuffing.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Temporary per-identity lockout after repeated failed logins.
/// Bound from <c>Auth:LoginLockout</c> (env vars: <c>Auth__LoginLockout__MaxFailures</c>,
/// <c>Auth__LoginLockout__LockoutSeconds</c>, <c>Auth__LoginLockout__Enabled</c>).
/// </summary>
public sealed class LoginLockoutOptions
{
    public const string SectionName = "Auth:LoginLockout";

    /// <summary>Consecutive failures (per username + system) before the identity is locked.</summary>
    public int MaxFailures { get; set; } = 5;

    public int LockoutSeconds { get; set; } = 300;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Browser CORS policy. The backend has no public URL — the dashboard BFF is same-origin — so the
/// list is empty by default and wildcard origins are never configured.
/// Bound from <c>Cors:AllowedOrigins</c> (comma-separated env var <c>Cors__AllowedOrigins</c>).
/// </summary>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = [];
}
