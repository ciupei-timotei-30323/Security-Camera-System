using System.Text;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services.Security;

/// <summary>
/// Refuses to boot a non-development host while sample/development secrets are still configured.
///
/// Shipping a container with the repository's dev defaults means the Pi API keys, the JWT signing
/// key and the factory-admin stub are all publicly known. Failing fast is the only safe outcome:
/// the alternative is a "working" deployment that any reader of the repository can take over.
/// </summary>
public static class ProductionSecretValidator
{
    /// <summary>Known placeholder secrets from <c>appsettings.Development.json</c>, <c>.env.example</c> and docs.</summary>
    private static readonly string[] ForbiddenExactSecrets =
    [
        "dev-only-api-key",
        "dev-only-internal-secret-change-me",
        "change-me",
        "change-me-too",
        "change-me-terminal-1",
        "change-me-terminal-2",
        "changeme",
        "replace-me",
        "CAM-0000-XXXX",
        "CAM-0000-DEV1"
    ];

    /// <summary>
    /// Prefixes that mark a development/sample secret regardless of the rest of the value. The
    /// <c>change-me*</c> family covers every placeholder shipped in <c>.env.example</c>, so an
    /// operator who copies the template and forgets one line is stopped at startup instead of
    /// running with a published password.
    /// </summary>
    private static readonly string[] ForbiddenSecretPrefixes =
    [
        "dev-only-",
        "dev-pi-key-",
        "dev-alert-terminal-key-",
        "factory-pass-",
        "dev-only-jwt-secret-",
        "change-me",
        "replace-me"
    ];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> listing every offending setting.
    /// Call this before <c>builder.Build()</c> so a misconfigured host never serves traffic.
    /// </summary>
    /// <param name="configuration">Host configuration.</param>
    /// <param name="isDevelopment">True in Development/Testing environments, which are exempt.</param>
    public static void Validate(IConfiguration configuration, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return;
        }

        var problems = new List<string>();

        var jwtSecret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(jwtSecret))
        {
            problems.Add("Jwt:Secret is not configured.");
        }
        else if (Encoding.UTF8.GetByteCount(jwtSecret) < JwtOptions.MinimumSecretBytes)
        {
            problems.Add(
                $"Jwt:Secret must be at least {JwtOptions.MinimumSecretBytes} bytes; the configured value is shorter.");
        }
        else if (IsForbiddenSecret(jwtSecret))
        {
            problems.Add("Jwt:Secret is still set to a development/sample value.");
        }

        CheckSecret(configuration["PiAuth:ApiKey"], "PiAuth:ApiKey", problems);

        foreach (var (systemId, apiKey) in ReadKeyedSecrets(configuration, "PiAuth:Systems"))
        {
            CheckSecret(apiKey, $"PiAuth:Systems:{systemId}", problems);
        }

        foreach (var (systemId, apiKey) in ReadKeyedSecrets(configuration, "PiAuth:AlertTerminalSystems"))
        {
            CheckSecret(apiKey, $"PiAuth:AlertTerminalSystems:{systemId}", problems);
        }

        var internalSecret = configuration["ExternalAuth:InternalSecret"];
        if (string.IsNullOrWhiteSpace(internalSecret))
        {
            problems.Add("ExternalAuth:InternalSecret is not configured.");
        }
        else if (IsForbiddenSecret(internalSecret))
        {
            problems.Add("ExternalAuth:InternalSecret is still set to a development/sample value.");
        }

        if (configuration.GetValue<bool?>("ExternalAuth:UseStub") == true)
        {
            problems.Add("ExternalAuth:UseStub must be false outside Development.");
        }

        if (ReadKeyedSecrets(configuration, "ExternalAuth:StubAccounts").Count > 0)
        {
            problems.Add("ExternalAuth:StubAccounts must be empty outside Development.");
        }

        // The device code is the built-in admin's initial password, so a shipped placeholder is
        // just as dangerous as a shipped API key.
        if (IsForbiddenSecret(configuration["Admin:DeviceCode"])
            || string.IsNullOrWhiteSpace(configuration["Admin:DeviceCode"]))
        {
            problems.Add("Admin:DeviceCode is missing or still set to a development/sample value.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Refusing to start: development/sample secrets are still configured for a " +
                $"non-Development environment ({EnvironmentNameDescription(configuration)}). " +
                "Set real secrets or use the Development environment. Problems: " +
                string.Join(" | ", problems));
        }
    }

    /// <summary>True when a value looks like a repository default, i.e. it is not secret at all.</summary>
    public static bool IsForbiddenSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (ForbiddenExactSecrets.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in ForbiddenSecretPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckSecret(string? value, string settingName, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (IsForbiddenSecret(value))
        {
            problems.Add($"{settingName} is still set to a development/sample value.");
        }
    }

    private static List<(string Key, string? Value)> ReadKeyedSecrets(
        IConfiguration configuration,
        string sectionPath)
    {
        var result = new List<(string, string?)>();

        foreach (var child in configuration.GetSection(sectionPath).GetChildren())
        {
            // StubAccounts children are objects (SystemId/Password/...); only leaf values matter.
            if (child.Value is not null)
            {
                result.Add((child.Key, child.Value));
                continue;
            }

            if (sectionPath == "ExternalAuth:StubAccounts")
            {
                // Any configured stub account is a problem; the caller only needs the count.
                result.Add((child.Key, "configured"));
            }
        }

        return result;
    }

    private static string EnvironmentNameDescription(IConfiguration configuration) =>
        configuration["ASPNETCORE_ENVIRONMENT"]
        ?? configuration["DOTNET_ENVIRONMENT"]
        ?? "Production";
}
