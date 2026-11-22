using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Services.ExternalAuth;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

public sealed class ExternalAdminAuthClientTests
{
    private const string InternalSecret = "test-internal-secret";

    [Fact]
    public async Task ValidateAdmin_SendsInternalSecretAndMapsCamelCaseResponse()
    {
        string? capturedSecret = null;
        string? capturedPath = null;
        string? capturedBody = null;

        var handler = new DelegateHandler(async request =>
        {
            capturedSecret = request.Headers.TryGetValues("X-Internal-Secret", out var values)
                ? values.Single()
                : null;
            capturedPath = request.RequestUri?.AbsolutePath;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    systemId = "sys-001",
                    username = "cam_admin_1",
                    piApiKey = "dev-pi-key-sys-001"
                })
            };
        });

        var client = CreateClient(handler, InternalSecret);

        var result = await client.ValidateAdminAsync("cam_admin_1", "factory-pass-sys-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("sys-001", result!.SystemId);
        Assert.Equal("cam_admin_1", result.Username);
        Assert.Equal("dev-pi-key-sys-001", result.PiApiKey);
        Assert.Equal(InternalSecret, capturedSecret);
        Assert.Equal("/api/internal/validate-admin", capturedPath);
        Assert.Contains("\"username\":\"cam_admin_1\"", capturedBody);
        Assert.Contains("\"password\":\"factory-pass-sys-001\"", capturedBody);
    }

    [Fact]
    public async Task ValidateAdmin_OmitsInternalSecretWhenNotConfigured()
    {
        string? capturedSecret = "not-called";

        var handler = new DelegateHandler(request =>
        {
            capturedSecret = request.Headers.TryGetValues("X-Internal-Secret", out var values)
                ? values.Single()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        var client = CreateClient(handler, internalSecret: string.Empty);

        var result = await client.ValidateAdminAsync("cam_admin_1", "wrong-password", CancellationToken.None);

        Assert.Null(result);
        Assert.Null(capturedSecret);
    }

    [Fact]
    public async Task ValidateAdmin_UnauthorizedResponse_ReturnsNull()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var client = CreateClient(handler, InternalSecret);

        var result = await client.ValidateAdminAsync("cam_admin_1", "wrong-password", CancellationToken.None);

        Assert.Null(result);
    }

    private static HttpExternalAdminAuthClient CreateClient(
        HttpMessageHandler handler,
        string internalSecret)
    {
        var options = Options.Create(new ExternalAuthOptions
        {
            BaseUrl = "http://external-auth-service:3001",
            InternalSecret = internalSecret
        });

        return new HttpExternalAdminAuthClient(
            new StubHttpClientFactory(handler),
            options,
            NullLogger<HttpExternalAdminAuthClient>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://external-auth-service:3001")
        };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
