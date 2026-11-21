using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class UsersEndpointsTests(IntegrationTestFactory factory)
{
    [Fact]
    public async Task DeleteUser_AdminDeletesSubUser_Succeeds()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        var user = await CreateSubUserAsync(admin, role: "User");

        var response = await admin.DeleteAsync($"/api/users/{user.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await admin.GetAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_CrossTenantId_ReturnsNotFound()
    {
        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password);

        var sys2User = await CreateSubUserAsync(admin2, role: "User");

        var response = await admin1.DeleteAsync($"/api/users/{sys2User.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillThere = await admin2.GetAsync($"/api/users/{sys2User.Id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_SelfDeletion_ReturnsBadRequest()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        var username = UniqueUsername();
        const string password = "self-delete-pass";

        var created = await CreateSubUserAsync(admin, role: "Admin", username: username, password: password);

        using var selfClient = factory.CreateClient();
        var loginResponse = await selfClient.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password,
            systemId = "sys-001"
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        selfClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.GetProperty("token").GetString());

        var response = await selfClient.DeleteAsync($"/api/users/{created.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_FactoryAdmin_ReturnsBadRequest()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        int factoryAdminId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            factoryAdminId = await dbContext.Users
                .Where(u => u.SystemId == CameraSystem.DefaultSystemId &&
                            u.Username == AdminOptions.AdminUsername)
                .Select(u => u.Id)
                .SingleAsync();
        }

        var response = await admin.DeleteAsync($"/api/users/{factoryAdminId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteUser_AsNonAdmin_ReturnsForbidden()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        var username = UniqueUsername();
        const string password = "non-admin-pass";

        var loginUser = await CreateSubUserAsync(
            admin, role: "User", username: username, password: password);
        var target = await CreateSubUserAsync(admin, role: "User");

        using var userClient = factory.CreateClient();
        var loginResponse = await userClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = loginUser.Username,
            password,
            systemId = "sys-001"
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.GetProperty("token").GetString());

        var response = await userClient.DeleteAsync($"/api/users/{target.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpClient> CreateAdminClientAsync(string username, string password)
    {
        var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.GetProperty("token").GetString());

        return client;
    }

    private static async Task<Security_Camera_Backend.Contracts.UserSummaryResponse> CreateSubUserAsync(
        HttpClient adminClient,
        string role,
        string? username = null,
        string? password = null)
    {
        var response = await adminClient.PostAsJsonAsync("/api/users", new
        {
            username = username ?? UniqueUsername(),
            password = password ?? "sub-user-pass",
            role
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content
            .ReadFromJsonAsync<Security_Camera_Backend.Contracts.UserSummaryResponse>();

        Assert.NotNull(created);
        return created!;
    }

    private static string UniqueUsername() => $"user_{Guid.NewGuid():N}"[..20];
}
