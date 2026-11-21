using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Security_Camera_Backend.Contracts;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class MultiTenancyEndpointsTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task FactoryAdmin_LoginUsesExternalStub_AndMeReturnsSystemId()
    {
        using var client = factory.CreateClient();

        var login = await LoginAsync(
            client,
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        Assert.Null(login.User.Id);
        Assert.Equal(IntegrationTestFactory.FactoryAdmin1, login.User.Username);
        Assert.Equal("Admin", login.User.Role);
        Assert.Equal("sys-001", login.User.SystemId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var profile = await meResponse.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Null(profile!.Id);
        Assert.Null(profile.CreatedAtUtc);
        Assert.Equal(IntegrationTestFactory.FactoryAdmin1, profile.Username);
        Assert.Equal("Admin", profile.Role);
        Assert.Equal("sys-001", profile.SystemId);
    }

    [Fact]
    public async Task FactoryAdmin_LoginWithMismatchedSystemId_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = IntegrationTestFactory.FactoryAdmin1,
            password = IntegrationTestFactory.FactoryAdmin1Password,
            systemId = "sys-002"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_SameUsernameIsAllowedInDifferentSystems_ButNotWithinOneSystem()
    {
        var username = UniqueUsername();

        var (admin1, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1, IntegrationTestFactory.FactoryAdmin1Password, "sys-001");
        var (admin2, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2, IntegrationTestFactory.FactoryAdmin2Password, "sys-002");

        using var _admin1 = admin1;
        using var _admin2 = admin2;

        var created1 = await CreateSubUserAsync(admin1, username, "password-1");
        var created2 = await CreateSubUserAsync(admin2, username, "password-2");

        Assert.Equal("sys-001", created1.SystemId);
        Assert.Equal("sys-002", created2.SystemId);
        Assert.Equal(username, created1.Username);
        Assert.Equal(username, created2.Username);

        var duplicate = await admin1.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "password-3"
        });

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var users1 = await admin1.GetFromJsonAsync<List<UserSummaryResponse>>("/api/users");
        var users2 = await admin2.GetFromJsonAsync<List<UserSummaryResponse>>("/api/users");

        Assert.NotNull(users1);
        Assert.NotNull(users2);
        Assert.Contains(users1!, u => u.Username == username && u.SystemId == "sys-001");
        Assert.DoesNotContain(users1!, u => u.SystemId == "sys-002");
        Assert.Contains(users2!, u => u.Username == username && u.SystemId == "sys-002");
        Assert.DoesNotContain(users2!, u => u.SystemId == "sys-001");
    }

    [Fact]
    public async Task Login_WithUsernameInTwoSystems_RequiresSystemId_AndScopesTheToken()
    {
        var username = UniqueUsername();
        const string password = "shared-password";

        var (admin1, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1, IntegrationTestFactory.FactoryAdmin1Password, "sys-001");
        var (admin2, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2, IntegrationTestFactory.FactoryAdmin2Password, "sys-002");

        using var _admin1 = admin1;
        using var _admin2 = admin2;

        await CreateSubUserAsync(admin1, username, password);
        await CreateSubUserAsync(admin2, username, password);

        using var anonymousClient = factory.CreateClient();

        var ambiguousResponse = await anonymousClient.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password
        });

        Assert.Equal(HttpStatusCode.BadRequest, ambiguousResponse.StatusCode);

        var problem = await ambiguousResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "SYSTEM_ID_REQUIRED",
            problem.GetProperty("code").GetString());
        Assert.Contains(
            "Multiple systems have a user with this username",
            problem.GetProperty("title").GetString());

        var login1 = await LoginAsync(anonymousClient, username, password, "sys-001");
        Assert.Equal("sys-001", login1.User.SystemId);
        Assert.Equal("User", login1.User.Role);
        Assert.NotNull(login1.User.Id);

        var login2 = await LoginAsync(anonymousClient, username, password, "sys-002");
        Assert.Equal("sys-002", login2.User.SystemId);
        Assert.NotEqual(login1.User.Id, login2.User.Id);

        var wrongSystem = await anonymousClient.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password,
            systemId = "sys-404"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSystem.StatusCode);
    }

    [Fact]
    public async Task PiKeys_AndEntranceLogs_AreIsolatedPerSystem()
    {
        // A DB-mapped key is created by the successful external-admin login.
        var (admin1, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1, IntegrationTestFactory.FactoryAdmin1Password, "sys-001");
        var (admin2, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2, IntegrationTestFactory.FactoryAdmin2Password, "sys-002");

        using var _admin1 = admin1;
        using var _admin2 = admin2;

        var capturedAt = DateTimeOffset.UtcNow;
        capturedAt = new DateTimeOffset(
            capturedAt.Ticks - capturedAt.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        var sys2Response = await PostFrameAsync("test-pi-api-key-sys-002", capturedAt);
        Assert.Equal(HttpStatusCode.Created, sys2Response.StatusCode);

        var sys1Response = await PostFrameAsync("test-factory-pi-key-sys-001", capturedAt);
        Assert.Equal(HttpStatusCode.Created, sys1Response.StatusCode);

        var sys1Events = await admin1.GetFromJsonAsync<List<DetectionEventResponse>>("/api/events?limit=500");
        var sys2Events = await admin2.GetFromJsonAsync<List<DetectionEventResponse>>("/api/events?limit=500");

        Assert.NotNull(sys1Events);
        Assert.NotNull(sys2Events);

        Assert.Contains(sys1Events!, e =>
            e.SystemId == "sys-001" &&
            e.Timestamp.ToUnixTimeMilliseconds() == capturedAt.ToUnixTimeMilliseconds());
        Assert.DoesNotContain(sys1Events!, e => e.SystemId == "sys-002");

        Assert.Contains(sys2Events!, e =>
            e.SystemId == "sys-002" &&
            e.Timestamp.ToUnixTimeMilliseconds() == capturedAt.ToUnixTimeMilliseconds());
        Assert.DoesNotContain(sys2Events!, e => e.SystemId == "sys-001");
    }

    [Fact]
    public async Task AwayModeSettings_AreScopedPerSystem_AndAffectNewEvents()
    {
        var (admin1, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1, IntegrationTestFactory.FactoryAdmin1Password, "sys-001");
        var (admin2, _) = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2, IntegrationTestFactory.FactoryAdmin2Password, "sys-002");

        using var _admin1 = admin1;
        using var _admin2 = admin2;

        try
        {
            var update = await admin1.PutAsJsonAsync("/api/away-mode-settings", new { isArmed = false });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            var setting1 = await admin1.GetFromJsonAsync<AwayModeSettingResponse>("/api/away-mode-settings");
            var setting2 = await admin2.GetFromJsonAsync<AwayModeSettingResponse>("/api/away-mode-settings");

            Assert.NotNull(setting1);
            Assert.NotNull(setting2);
            Assert.False(setting1!.IsArmed);
            Assert.Equal("sys-001", setting1.SystemId);
            Assert.True(setting2!.IsArmed);
            Assert.Equal("sys-002", setting2.SystemId);

            var capturedAt = DateTimeOffset.UtcNow;
            var response = await PostFrameAsync(IntegrationTestFactory.PiApiKey, capturedAt);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var events = await admin1.GetFromJsonAsync<List<DetectionEventResponse>>("/api/events?limit=500");
            var newEvent = Assert.Single(events!, e =>
                e.SystemId == "sys-001" &&
                e.Timestamp.ToUnixTimeMilliseconds() == capturedAt.ToUnixTimeMilliseconds());

            Assert.False(newEvent.IsSuspect);
        }
        finally
        {
            // Other tests expect the default armed state.
            await admin1.PutAsJsonAsync("/api/away-mode-settings", new { isArmed = true });
        }
    }

    private async Task<HttpResponseMessage> PostFrameAsync(string apiKey, DateTimeOffset capturedAt)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(JpegBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(image, "image", "frame.jpg");
        form.Add(
            new StringContent(capturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            "capturedAt");
        form.Add(new StringContent("false"), "isOfflineSync");

        return await client.PostAsync("/api/events", form);
    }

    private async Task<LoginResponse> LoginAsync(
        HttpClient client,
        string username,
        string password,
        string? systemId = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password,
            systemId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.NotNull(login);
        return login!;
    }

    private async Task<(HttpClient Client, LoginResponse Login)> CreateAdminClientAsync(
        string username,
        string password,
        string systemId)
    {
        var client = factory.CreateClient();
        var login = await LoginAsync(client, username, password, systemId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        return (client, login);
    }

    private static async Task<UserSummaryResponse> CreateSubUserAsync(
        HttpClient adminClient,
        string username,
        string password)
    {
        var response = await adminClient.PostAsJsonAsync("/api/users", new
        {
            username,
            password,
            role = "User"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<UserSummaryResponse>();

        Assert.NotNull(created);
        return created!;
    }

    private static string UniqueUsername() => $"tenant_{Guid.NewGuid():N}"[..20];
}
