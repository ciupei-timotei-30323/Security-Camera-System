using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Security_Camera_Backend.Contracts;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AuthEndpointsTests(IntegrationTestFactory factory)
{
    [Fact]
    public async Task Login_WithValidAdminCredentials_ReturnsTokenAndUser()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = IntegrationTestFactory.AdminDeviceCode
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login!.Token));
        Assert.Equal(DateTimeKind.Utc, login.ExpiresAtUtc.Kind);
        Assert.True(login.ExpiresAtUtc > DateTime.UtcNow);
        Assert.True(login.User.Id > 0);
        Assert.Equal("admin", login.User.Username);
        Assert.Equal("Admin", login.User.Role);
    }

    [Theory]
    [InlineData("admin", "definitely-the-wrong-password")]
    [InlineData("no-such-user", IntegrationTestFactory.AdminDeviceCode)]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized(string username, string password)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"username":"","password":"TEST-CAM-1234"}""")]
    [InlineData("""{"username":"admin","password":""}""")]
    [InlineData("""{"username":"   ","password":"TEST-CAM-1234"}""")]
    public async Task Login_WithMissingOrBlankFields_ReturnsBadRequest(string json)
    {
        using var client = factory.CreateClient();

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsAdminProfile()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>();

        Assert.NotNull(profile);
        Assert.Equal(login.User.Id, profile!.Id);
        Assert.Equal("admin", profile.Username);
        Assert.Equal("Admin", profile.Role);
        Assert.NotEqual(default, profile.CreatedAtUtc);
        Assert.True(profile.CreatedAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesToken_AndBlocksSubsequentRequests()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Token);

        var logoutResponse = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        var logout = await logoutResponse.Content.ReadFromJsonAsync<LogoutResponse>();
        Assert.NotNull(logout);
        Assert.Equal("Logged out successfully.", logout!.Message);

        // The same token must now be rejected by the jti deny-list.
        var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);

        var secondLogoutResponse = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, secondLogoutResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutToken_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = IntegrationTestFactory.AdminDeviceCode
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();

        Assert.NotNull(login);
        return login!;
    }
}
