using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Security_Camera_Backend.Services.Security;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>
/// Guards the fail-fast checks: a host that is not Development must refuse to boot while the
/// repository's sample secrets are still configured.
/// </summary>
public sealed class ProductionSecretValidationTests
{
    private const string StrongJwtSecret = "kM4Q2x9pR7tL1vB6nJ8sH3dF0gW5yZ2cU4aE7iO9pQ1rT6uX8sV3bN5mK0jH2gF4dS";

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> SafeSecrets() => new()
    {
        ["Jwt:Secret"] = StrongJwtSecret,
        ["PiAuth:ApiKey"] = "pi-key-" + Guid.NewGuid().ToString("N"),
        ["ExternalAuth:InternalSecret"] = "internal-" + Guid.NewGuid().ToString("N"),
        ["ExternalAuth:UseStub"] = "false",
        ["Admin:DeviceCode"] = "CAM-8842-ZX19"
    };

    [Fact]
    public void Validate_WithStrongSecrets_DoesNotThrow()
    {
        ProductionSecretValidator.Validate(BuildConfiguration(SafeSecrets()), isDevelopment: false);
    }

    [Fact]
    public void Validate_IsSkippedInDevelopment()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "dev-only-jwt-secret-change-me-0123456789abcdef",
            ["PiAuth:ApiKey"] = "dev-only-api-key",
            ["ExternalAuth:UseStub"] = "true"
        });

        ProductionSecretValidator.Validate(configuration, isDevelopment: true);
    }

    [Theory]
    [InlineData("dev-only-api-key")]
    [InlineData("dev-pi-key-sys-001")]
    [InlineData("dev-alert-terminal-key-sys-001")]
    [InlineData("dev-only-jwt-secret-change-me-0123456789abcdef")]
    [InlineData("factory-pass-sys-001")]
    [InlineData("change-me")]
    public void Validate_WithDevelopmentSampleSecrets_Throws(string secret)
    {
        var values = SafeSecrets();
        values["Jwt:Secret"] = secret;

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("Jwt:Secret", exception.Message);
    }

    [Fact]
    public void Validate_WithShortJwtSecret_Throws()
    {
        var values = SafeSecrets();
        values["Jwt:Secret"] = "too-short";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("at least 32 bytes", exception.Message);
    }

    [Fact]
    public void Validate_WithDefaultSamplePiKey_Throws()
    {
        var values = SafeSecrets();
        values["PiAuth:Systems:sys-002"] = "dev-only-api-key-sys-002";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("PiAuth:Systems:sys-002", exception.Message);
    }

    [Fact]
    public void Validate_WithDefaultAlertTerminalKey_Throws()
    {
        var values = SafeSecrets();
        values["PiAuth:AlertTerminalSystems:sys-001"] = "dev-alert-terminal-key-sys-001";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("PiAuth:AlertTerminalSystems:sys-001", exception.Message);
    }

    [Fact]
    public void Validate_WithExternalAuthStubEnabled_Throws()
    {
        var values = SafeSecrets();
        values["ExternalAuth:UseStub"] = "true";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("ExternalAuth:UseStub", exception.Message);
    }

    [Fact]
    public void Validate_WithSampleStubAccounts_Throws()
    {
        var values = SafeSecrets();
        values["ExternalAuth:StubAccounts:cam_admin_1:SystemId"] = "sys-001";
        values["ExternalAuth:StubAccounts:cam_admin_1:Password"] = "factory-pass-sys-001";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("ExternalAuth:StubAccounts", exception.Message);
    }

    [Fact]
    public void Validate_WithMissingDeviceCode_Throws()
    {
        var values = SafeSecrets();
        values.Remove("Admin:DeviceCode");

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("Admin:DeviceCode", exception.Message);
    }

    [Fact]
    public void Validate_WithCopyPastedEnvExampleSecret_Throws()
    {
        var values = SafeSecrets();
        values["PiAuth:ApiKey"] = "change-me";
        values["ExternalAuth:InternalSecret"] = "change-me-to-a-long-random-internal-secret";

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("PiAuth:ApiKey", exception.Message);
    }

    [Fact]
    public void Validate_WithMissingInternalSecret_Throws()
    {
        var values = SafeSecrets();
        values.Remove("ExternalAuth:InternalSecret");

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionSecretValidator.Validate(BuildConfiguration(values), isDevelopment: false));

        Assert.Contains("ExternalAuth:InternalSecret", exception.Message);
    }

    /// <summary>
    /// End-to-end: the real host, started in the Production environment while the shared test
    /// configuration still enables the external-auth stub, must refuse to start instead of
    /// serving traffic. (The specific messages are asserted by the tests above; here only the
    /// refusal to boot matters, because program-level startup failures are logged rather than
    /// rethrown by the bootstrap try/catch.)
    /// </summary>
    [Fact]
    public void Host_StartedInProductionWithDevelopmentSettings_FailsToStart()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");

                // Pinned explicitly so the assertion does not depend on whatever the rest of the
                // suite happened to export into the process environment.
                builder.UseSetting("Jwt:Secret", "dev-only-jwt-secret-change-me-0123456789abcdef");
                builder.UseSetting("ExternalAuth:UseStub", "true");
            });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

}
