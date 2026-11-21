using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Security;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class CameraEndpointsTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task LatestFrame_IsTenantIsolated()
    {
        var sys1Frame = JpegBytes.Concat(new byte[] { 0x11 }).ToArray();
        var sys3Frame = JpegBytes.Concat(new byte[] { 0x33 }).ToArray();

        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);
        using var admin3 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin3,
            IntegrationTestFactory.FactoryAdmin3Password);

        using var sys1Pi = CreatePiClient(IntegrationTestFactory.PiApiKey);
        using var sys3Pi = CreatePiClient(IntegrationTestFactory.PiApiKeySys003);

        var sys1Upload = await PostFrameAsync(sys1Pi, sys1Frame);
        Assert.Equal(HttpStatusCode.Accepted, sys1Upload.StatusCode);

        var sys3Upload = await PostFrameAsync(sys3Pi, sys3Frame);
        Assert.Equal(HttpStatusCode.Accepted, sys3Upload.StatusCode);

        var sys1Latest = await admin1.GetAsync("/api/camera/latest");
        Assert.Equal(HttpStatusCode.OK, sys1Latest.StatusCode);
        Assert.Equal(sys1Frame, await sys1Latest.Content.ReadAsByteArrayAsync());

        var sys3Latest = await admin3.GetAsync("/api/camera/latest");
        Assert.Equal(HttpStatusCode.OK, sys3Latest.StatusCode);
        Assert.Equal(sys3Frame, await sys3Latest.Content.ReadAsByteArrayAsync());

        // sys-001 must never receive the sys-003 frame.
        Assert.NotEqual(sys3Frame, await sys1Latest.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UploadFrame_ValidatesContentTypeAndSize()
    {
        using var pi = CreatePiClient(IntegrationTestFactory.PiApiKeySys003);

        using var notJpeg = new ByteArrayContent(JpegBytes);
        notJpeg.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var wrongType = await pi.PostAsync("/api/camera/frame", notJpeg);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);

        using var tooLargeBody = new ByteArrayContent(
            new byte[Security_Camera_Backend.Controllers.CameraController.MaxFrameBytes + 1]);
        tooLargeBody.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var tooLarge = await pi.PostAsync("/api/camera/frame", tooLargeBody);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using var empty = new ByteArrayContent([]);
        empty.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var emptyResponse = await pi.PostAsync("/api/camera/frame", empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
    }

    [Fact]
    public async Task UploadFrame_IsRateLimitedPerApiKey()
    {
        using var pi = CreatePiClient(IntegrationTestFactory.PiApiKeyRateLimit);

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var content = new ByteArrayContent(JpegBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var response = await pi.PostAsync("/api/camera/frame", content);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(5, statuses.Count(status => status == HttpStatusCode.Accepted));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Stream_ReturnsMjpegAndStopsWhenClientDisconnects()
    {
        var frame = JpegBytes.Concat(new byte[] { 0x77 }).ToArray();
        using var pi = CreatePiClient(IntegrationTestFactory.PiApiKey);
        var upload = await PostFrameAsync(pi, frame);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);

        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await admin.GetAsync("/api/camera/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("multipart/x-mixed-replace", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer, cts.Token);
        var prefix = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.Contains("--frame", prefix);
        Assert.Contains("Content-Type: image/jpeg", prefix);
    }

    [Fact]
    public async Task AuthenticatedDeviceRequest_UpdatesLastSeenAt_AtMostOncePerFiveSeconds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var systemId = $"sys-heartbeat-{suffix}";
        var apiKey = $"heartbeat-key-{suffix}";
        var now = DateTime.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            dbContext.CameraSystems.Add(new CameraSystem
            {
                SystemId = systemId,
                DisplayName = "Heartbeat test",
                CreatedAtUtc = now
            });
            dbContext.PiDevices.Add(new PiDevice
            {
                SystemId = systemId,
                ApiKeyHash = PiApiKeyHasher.HashToHex(apiKey),
                Name = "Heartbeat test camera",
                DeviceType = DeviceType.Camera,
                CreatedAtUtc = now
            });
            await dbContext.SaveChangesAsync();
        }

        using var pi = CreatePiClient(apiKey);
        var first = await PostFrameAsync(pi, JpegBytes);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        DateTime? firstSeen;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            firstSeen = await dbContext.PiDevices
                .Where(device => device.SystemId == systemId)
                .Select(device => device.LastSeenAt)
                .SingleAsync();
        }

        Assert.NotNull(firstSeen);
        Assert.True(firstSeen > now.AddSeconds(-1));

        var second = await PostFrameAsync(pi, JpegBytes);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            var secondSeen = await dbContext.PiDevices
                .Where(device => device.SystemId == systemId)
                .Select(device => device.LastSeenAt)
                .SingleAsync();

            Assert.Equal(firstSeen, secondSeen);
        }
    }

    [Fact]
    public async Task SystemStatus_ReportsTenantScopedOnlineState()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

            await dbContext.PiDevices
                .Where(device => device.SystemId == "sys-001")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(device => device.LastSeenAt, now));

            await dbContext.PiDevices
                .Where(device => device.SystemId == "sys-002")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(device => device.LastSeenAt, now.AddMinutes(-1)));

            dbContext.PiDevices.AddRange(
                new PiDevice
                {
                    SystemId = "sys-001",
                    ApiKeyHash = PiApiKeyHasher.HashToHex($"terminal-1-{suffix}"),
                    Name = "sys-001 terminal",
                    DeviceType = DeviceType.AlertTerminal,
                    LastSeenAt = now,
                    CreatedAtUtc = now
                },
                new PiDevice
                {
                    SystemId = "sys-002",
                    ApiKeyHash = PiApiKeyHasher.HashToHex($"terminal-2-{suffix}"),
                    Name = "sys-002 terminal",
                    DeviceType = DeviceType.AlertTerminal,
                    LastSeenAt = now.AddMinutes(-1),
                    CreatedAtUtc = now
                });

            await dbContext.SaveChangesAsync();
        }

        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password);

        var status1 = await admin1.GetFromJsonAsync<JsonElement>("/api/system/status");
        var status2 = await admin2.GetFromJsonAsync<JsonElement>("/api/system/status");

        Assert.True(status1.GetProperty("camera").GetProperty("online").GetBoolean());
        Assert.True(status1.GetProperty("alertTerminal").GetProperty("online").GetBoolean());
        Assert.Equal("ok", status1.GetProperty("backend").GetString());
        Assert.Equal("ok", status1.GetProperty("cvService").GetString());

        Assert.False(status2.GetProperty("camera").GetProperty("online").GetBoolean());
        Assert.False(status2.GetProperty("alertTerminal").GetProperty("online").GetBoolean());
        Assert.Equal("ok", status2.GetProperty("backend").GetString());
        Assert.Equal("ok", status2.GetProperty("cvService").GetString());
    }

    [Fact]
    public async Task LatestAndStatus_RequireJwt()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/camera/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/camera/stream")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/camera/frame", new ByteArrayContent(JpegBytes))).StatusCode);
    }

    private HttpClient CreatePiClient(string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static async Task<HttpResponseMessage> PostFrameAsync(HttpClient client, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return await client.PostAsync("/api/camera/frame", content);
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
}
