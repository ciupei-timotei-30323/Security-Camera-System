using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Security_Camera_Backend.Contracts;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>
/// MediaMTX posts here for every stream <c>publish</c> and <c>read</c>. The path is the
/// <c>SystemId</c>, so these tests are the multi-tenancy guarantee for video: a credential for one
/// system must never open another system's stream, and an alert-terminal key must never publish.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class MediaMtxAuthEndpointsTests(IntegrationTestFactory factory)
{
    private const string AuthPath = "/api/internal/mediamtx-auth";

    [Fact]
    public async Task Publish_WithCameraKeyForMatchingPath_IsAllowed()
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "sys-001",
            password = IntegrationTestFactory.PiApiKey,
            protocol = "rtsp"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Publish_AcceptsLeadingSlashAndQueryToken()
    {
        using var client = factory.CreateClient();

        var leadingSlash = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "/sys-001",
            password = IntegrationTestFactory.PiApiKey
        });

        Assert.Equal(HttpStatusCode.OK, leadingSlash.StatusCode);

        var queryToken = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "sys-001",
            query = $"token={IntegrationTestFactory.PiApiKey}"
        });

        Assert.Equal(HttpStatusCode.OK, queryToken.StatusCode);
    }

    [Fact]
    public async Task Publish_WithCrossTenantCameraKey_IsRejected()
    {
        using var client = factory.CreateClient();

        // sys-002's key against sys-001's path: same broker, different tenant.
        var response = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "sys-001",
            password = IntegrationTestFactory.PiApiKeySys002
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publish_WithAlertTerminalKey_IsRejected()
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "sys-001",
            password = IntegrationTestFactory.AlertTerminalKeySys001
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publish_WithUnknownKey_IsRejected()
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "publish",
            path = "sys-001",
            password = "not-a-real-pi-key"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_WithMatchingDashboardToken_IsAllowedAndReturnsExpiry()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "sys-001");

        var response = await AuthorizeAsync(client, new
        {
            action = "read",
            path = "sys-001",
            token = login.Token
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<MediaMtxAuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.ExpireAt));

        var expireAt = DateTimeOffset.Parse(body.ExpireAt!);
        Assert.InRange(expireAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(60));
    }

    [Fact]
    public async Task Read_WithCrossTenantToken_IsRejected()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "sys-001");

        var response = await AuthorizeAsync(client, new
        {
            action = "read",
            path = "sys-002",
            token = login.Token
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_WithRevokedToken_IsRejected()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "sys-001");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;

        var response = await AuthorizeAsync(client, new
        {
            action = "read",
            path = "sys-001",
            token = login.Token
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_WithGarbageToken_IsRejected()
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "read",
            path = "sys-001",
            token = "not.a.jwt"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sys 001")]
    [InlineData("sys/001")]
    [InlineData("sys-001/../sys-002")]
    public async Task InvalidPath_IsRejected(string path)
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "publish",
            path,
            password = IntegrationTestFactory.PiApiKey
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedAction_IsRejected()
    {
        using var client = factory.CreateClient();

        var response = await AuthorizeAsync(client, new
        {
            action = "api",
            path = "sys-001",
            password = IntegrationTestFactory.PiApiKey
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> AuthorizeAsync(HttpClient client, object payload) =>
        client.PostAsJsonAsync(AuthPath, payload);

    private async Task<LoginResponse> LoginAsync(HttpClient client, string systemId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = IntegrationTestFactory.FactoryAdmin1,
            password = IntegrationTestFactory.FactoryAdmin1Password,
            systemId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.Equal(systemId, login!.User.SystemId);

        return login;
    }
}
