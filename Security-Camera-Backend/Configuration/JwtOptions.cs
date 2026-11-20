namespace Security_Camera_Backend.Configuration;

/// <summary>
/// JWT signing/validation settings for dashboard users.
/// Bound from <c>Jwt</c> (env vars: <c>Jwt__Secret</c>, <c>Jwt__Issuer</c>, <c>Jwt__Audience</c>, <c>Jwt__ExpirationMinutes</c>).
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public const int MinimumSecretBytes = 32;

    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "security-camera-backend";

    public string Audience { get; set; } = "security-camera-dashboard";

    public int ExpirationMinutes { get; set; } = 480;
}
