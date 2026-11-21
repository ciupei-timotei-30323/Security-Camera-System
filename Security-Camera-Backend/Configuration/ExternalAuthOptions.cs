namespace Security_Camera_Backend.Configuration;

/// <summary>
/// Factory-level administrator authentication service. In production this points at the
/// external factory IAM service; in development <see cref="UseStub"/> makes the API validate
/// against <see cref="StubAccounts"/> instead.
/// Bound from <c>ExternalAuth</c> (env vars: <c>ExternalAuth__UseStub</c>,
/// <c>ExternalAuth__BaseUrl</c>, <c>ExternalAuth__TimeoutSeconds</c>).
/// </summary>
public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    /// <summary>
    /// When true, <c>StubExternalAdminAuthClient</c> is used instead of the HTTP client.
    /// Defaults to true in Development and false elsewhere (see <c>Program.cs</c>).
    /// </summary>
    public bool UseStub { get; set; }

    public string BaseUrl { get; set; } = "http://external-auth:8080";

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Mock factory camera kits used by the stub. Key is the administrator username.</summary>
    public Dictionary<string, StubExternalAdminAccount> StubAccounts { get; set; } = new();
}

public sealed class StubExternalAdminAccount
{
    public string SystemId { get; set; } = string.Empty;

    /// <summary>Password expected by the stub. Never used by the HTTP implementation.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Optional camera kit key registered for the system after a successful login.</summary>
    public string? PiApiKey { get; set; }
}
