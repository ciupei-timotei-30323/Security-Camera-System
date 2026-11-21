using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Security_Camera_Backend.Services.Notifications;
using Testcontainers.PostgreSql;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>
/// Shares one PostgreSQL container, one application host and one captures directory across
/// every test class in the "Integration" collection.
/// </summary>
public sealed class IntegrationTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminDeviceCode = "TEST-CAM-1234";
    public const string JwtSecret = "super-secret-integration-test-key-at-least-32-chars";
    public const string PiApiKey = "test-pi-api-key-12345";
    public const string PiApiKeySys002 = "test-pi-api-key-sys-002";
    public const string FactoryAdmin1 = "cam_admin_1";
    public const string FactoryAdmin1Password = "factory-pass-sys-001";
    public const string FactoryAdmin2 = "cam_admin_2";
    public const string FactoryAdmin2Password = "factory-pass-sys-002";
    public const string FactoryAdmin3 = "cam_admin_3";
    public const string FactoryAdmin3Password = "factory-pass-sys-003";
    public const string PiApiKeySys003 = "test-pi-api-key-sys-003";
    public const string PiApiKeyRateLimit = "test-pi-rate-limit-key";
    public const string AlertTerminalKeySys001 = "test-alert-terminal-key-sys-001";
    public const string AlertTerminalKeySys002 = "test-alert-terminal-key-sys-002";
    public const string AlertTerminalKeySys003 = "test-alert-terminal-key-sys-003";

#pragma warning disable CS0618 // Testcontainers deprecated the parameterless builder ctor; the requested form is kept.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();
#pragma warning restore CS0618

    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public string CapturesDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"security-camera-captures-{Guid.NewGuid():N}");

    public FakeSmtpSender EmailSender { get; } = new();

    // Program reads DB_URL while registering services, i.e. before WebApplicationFactory can
    // apply ConfigureAppConfiguration callbacks. Process environment variables are therefore the
    // only override that is guaranteed to be visible to that code.
    private IReadOnlyDictionary<string, string> TestEnvironment => new Dictionary<string, string>
    {
        ["DB_URL"] = _postgres.GetConnectionString(),
        ["Admin__DeviceCode"] = AdminDeviceCode,
        ["Jwt__Secret"] = JwtSecret,
        ["PiAuth__ApiKey"] = PiApiKey,
        ["PiAuth__DefaultSystemId"] = "sys-001",
        ["PiAuth__Systems__sys-002"] = PiApiKeySys002,
        ["PiAuth__Systems__sys-003"] = PiApiKeySys003,
        ["PiAuth__Systems__sys-ratelimit"] = PiApiKeyRateLimit,
        ["PiAuth__AlertTerminalSystems__sys-001"] = AlertTerminalKeySys001,
        ["PiAuth__AlertTerminalSystems__sys-002"] = AlertTerminalKeySys002,
        ["PiAuth__AlertTerminalSystems__sys-003"] = AlertTerminalKeySys003,
        ["Notifications__EmailCooldownSeconds"] = "1",
        ["Notifications__DashboardBaseUrl"] = "http://dashboard.test",
        ["Notifications__EmailRetryBaseDelayMilliseconds"] = "10",
        ["Smtp__Host"] = "localhost",
        ["Smtp__Port"] = "1025",
        ["Smtp__FromAddress"] = "alerts@test.local",
        ["Alarm__DurationSeconds"] = "1",
        ["ExternalAuth__UseStub"] = "true",
        ["ExternalAuth__StubAccounts__cam_admin_1__SystemId"] = "sys-001",
        ["ExternalAuth__StubAccounts__cam_admin_1__Password"] = FactoryAdmin1Password,
        ["ExternalAuth__StubAccounts__cam_admin_1__PiApiKey"] = "test-factory-pi-key-sys-001",
        ["ExternalAuth__StubAccounts__cam_admin_2__SystemId"] = "sys-002",
        ["ExternalAuth__StubAccounts__cam_admin_2__Password"] = FactoryAdmin2Password,
        ["ExternalAuth__StubAccounts__cam_admin_2__PiApiKey"] = "test-factory-pi-key-sys-002",
        ["ExternalAuth__StubAccounts__cam_admin_3__SystemId"] = "sys-003",
        ["ExternalAuth__StubAccounts__cam_admin_3__Password"] = FactoryAdmin3Password,
        ["ExternalAuth__StubAccounts__cam_admin_3__PiApiKey"] = "test-factory-pi-key-sys-003",
        ["Storage__CapturesDirectory"] = CapturesDirectory,
        ["CvService__BaseUrl"] = "http://cv-service",
        ["ASPNETCORE_ENVIRONMENT"] = "Testing"
    };

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Directory.CreateDirectory(CapturesDirectory);

        foreach (var (key, value) in TestEnvironment)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // CvServiceClient is a typed HttpClient without a primary handler configured by the
            // application, so the default handler replaces the real cv-service call.
            services.ConfigureHttpClientDefaults(httpClient =>
                httpClient.ConfigurePrimaryHttpMessageHandler(() => new StubCvServiceHandler()));

            services.RemoveAll<ISmtpSender>();
            services.AddSingleton<ISmtpSender>(EmailSender);
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Shut the host down before the database and the captures volume disappear.
        await base.DisposeAsync();

        await _postgres.DisposeAsync();

        foreach (var (key, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            if (Directory.Exists(CapturesDirectory))
            {
                Directory.Delete(CapturesDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked handle must not fail the test run; the temp directory is best-effort cleanup.
        }
    }

    /// <summary>Deterministic stand-in for the Python cv-service's <c>POST /analyze</c>.</summary>
    private sealed class StubCvServiceHandler : HttpMessageHandler
    {
        private const string AnalysisJson = """
            {
              "person_detected": true,
              "face_detected": true,
              "confidence": 0.95,
              "person_confidence": 0.95,
              "face_confidence": 0.91,
              "face_crop_path": null,
              "annotated_path": null
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/health")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
                });
            }

            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/analyze")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AnalysisJson, Encoding.UTF8, "application/json")
            });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFactory>
{
    public const string Name = "Integration";
}
