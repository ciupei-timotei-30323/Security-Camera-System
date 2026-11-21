using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Security_Camera_Backend.Models;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class EventsEndpointsTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task CreateEvent_WithoutApiKey_ReturnsUnauthorized()
    {
        using var client = factory.CreateClient();
        using var form = CreateForm(JpegBytes);

        var response = await client.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_WithInvalidApiKey_ReturnsUnauthorized()
    {
        using var client = CreateClient("not-the-pi-api-key");
        using var form = CreateForm(JpegBytes);

        var response = await client.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_WithValidApiKeyButMissingImage_ReturnsBadRequest()
    {
        using var client = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(image: null);

        var response = await client.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_WithValidFrame_PersistsEventAndSavesImage()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddHours(-4);
        capturedAt = new DateTimeOffset(
            capturedAt.Ticks - capturedAt.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        var beforeRequest = DateTimeOffset.UtcNow.AddSeconds(-2);

        int? previousMaxId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            previousMaxId = await dbContext.DetectionEvents.MaxAsync(e => (int?)e.Id);
        }

        using var client = CreateClient(IntegrationTestFactory.PiApiKey);
        // capturedAt is intentionally ignored because isOfflineSync=false.
        using var form = CreateForm(JpegBytes, capturedAt, isOfflineSync: false);

        var response = await client.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Detection event recorded.", body.GetProperty("message").GetString());

        await using var assertScope = factory.Services.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

        var newEvents = await assertDb.DetectionEvents
            .AsNoTracking()
            .Where(e => e.Id > (previousMaxId ?? 0))
            .ToListAsync();

        var detectionEvent = Assert.Single(newEvents);

        Assert.Equal("sys-001", detectionEvent.SystemId);
        Assert.True(detectionEvent.Timestamp >= beforeRequest);
        Assert.NotEqual(
            capturedAt.ToUnixTimeSeconds(),
            detectionEvent.Timestamp.ToUnixTimeSeconds());
        Assert.True(detectionEvent.IsSuspect);
        Assert.False(detectionEvent.IsOfflineSync);
        Assert.False(detectionEvent.IsEmailSent);
        Assert.Equal(0.95f, detectionEvent.Confidence);
        Assert.Null(detectionEvent.DetectedFaceUri);
        Assert.NotNull(detectionEvent.FullImageUri);
        Assert.DoesNotContain("/", detectionEvent.FullImageUri);
        Assert.StartsWith("raw_", detectionEvent.FullImageUri);
        Assert.EndsWith(".jpg", detectionEvent.FullImageUri);

        var savedImagePath = Path.Combine(factory.CapturesDirectory, detectionEvent.FullImageUri!);

        Assert.True(File.Exists(savedImagePath), $"Expected the raw frame at '{savedImagePath}'.");
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(savedImagePath));
    }

    [Fact]
    public async Task CreateEvent_OfflineSync_UsesCapturedAtAsTimestamp()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        capturedAt = new DateTimeOffset(
            capturedAt.Ticks - capturedAt.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        using var client = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(JpegBytes, capturedAt, isOfflineSync: true);

        var response = await client.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var returned = body.GetProperty("event");

        Assert.Equal(capturedAt.ToUnixTimeMilliseconds(), returned.GetProperty("timestamp").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task CreateEvent_RetryWithSameClientEventId_ReturnsExistingEventWithoutDuplicate()
    {
        var clientEventId = Guid.NewGuid();

        using var client = CreateClient(IntegrationTestFactory.PiApiKey);

        using var firstForm = CreateForm(JpegBytes, clientEventId: clientEventId);
        var firstResponse = await client.PostAsync("/api/events", firstForm);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        var firstBody = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("event").GetProperty("id").GetInt32();

        using var secondForm = CreateForm(JpegBytes, clientEventId: clientEventId);
        var secondResponse = await client.PostAsync("/api/events", secondForm);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var secondBody = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstId, secondBody.GetProperty("event").GetProperty("id").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

        var stored = await dbContext.DetectionEvents
            .AsNoTracking()
            .Where(e => e.ClientEventId == clientEventId)
            .ToListAsync();

        Assert.Single(stored);
        Assert.Equal("sys-001", stored[0].SystemId);
    }

    [Fact]
    public async Task GetPaged_SupportsPaginationFiltersAndNewestFirst()
    {
        var baseTime = DateTimeOffset.UtcNow.AddDays(-40);
        baseTime = new DateTimeOffset(
            baseTime.Ticks - baseTime.Ticks % TimeSpan.TicksPerSecond,
            TimeSpan.Zero);

        // A window unique to this test keeps the shared database out of the assertions.
        var from = baseTime;
        var to = baseTime.AddMinutes(2);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

            dbContext.DetectionEvents.AddRange(
                NewEvent(baseTime, isSuspect: false),
                NewEvent(baseTime.AddMinutes(1), isSuspect: true),
                NewEvent(baseTime.AddMinutes(2), isSuspect: true));

            await dbContext.SaveChangesAsync();
        }

        using var client = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        var response = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            "&page=1&pageSize=2");

        Assert.Equal(3, response.GetProperty("total").GetInt32());
        Assert.Equal(1, response.GetProperty("page").GetInt32());
        Assert.Equal(2, response.GetProperty("pageSize").GetInt32());

        var items = response.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(baseTime.AddMinutes(2).ToUnixTimeMilliseconds(),
            items[0].GetProperty("timestamp").GetDateTimeOffset().ToUnixTimeMilliseconds());

        var suspectOnly = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            "&suspectOnly=true");

        Assert.Equal(2, suspectOnly.GetProperty("total").GetInt32());

        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/events?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            "&page=2&pageSize=2");

        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GetByIdAndDelete_AreTenantScoped_AndDeleteRemovesFiles()
    {
        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password);

        using var piClient = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(JpegBytes);
        var createResponse = await piClient.PostAsync("/api/events", form);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var eventId = created.GetProperty("event").GetProperty("id").GetInt32();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            var stored = await dbContext.DetectionEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
            Assert.True(File.Exists(Path.Combine(factory.CapturesDirectory, stored.FullImageUri!)));
        }

        var ownResponse = await admin1.GetAsync($"/api/events/{eventId}");
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);

        var crossTenantGet = await admin2.GetAsync($"/api/events/{eventId}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantGet.StatusCode);

        var crossTenantDelete = await admin2.DeleteAsync($"/api/events/{eventId}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantDelete.StatusCode);

        var deleteResponse = await admin1.DeleteAsync($"/api/events/{eventId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var deletedGet = await admin1.GetAsync($"/api/events/{eventId}");
        Assert.Equal(HttpStatusCode.NotFound, deletedGet.StatusCode);

        await using var scope2 = factory.Services.CreateAsyncScope();
        var dbContext2 = scope2.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
        Assert.False(await dbContext2.DetectionEvents.AnyAsync(e => e.Id == eventId));
    }

    [Fact]
    public async Task FaceAndAnnotatedEndpoints_ServePrivateTenantScopedImages()
    {
        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password);

        using var piClient = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(JpegBytes);
        var createResponse = await piClient.PostAsync("/api/events", form);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var eventId = created.GetProperty("event").GetProperty("id").GetInt32();

        var annotated = await admin1.GetAsync($"/api/events/{eventId}/annotated");
        Assert.Equal(HttpStatusCode.OK, annotated.StatusCode);
        Assert.Equal("image/jpeg", annotated.Content.Headers.ContentType?.MediaType);
        Assert.Contains("private", annotated.Headers.CacheControl?.ToString());
        Assert.Equal(JpegBytes, await annotated.Content.ReadAsByteArrayAsync());

        var faceMissing = await admin1.GetAsync($"/api/events/{eventId}/face");
        Assert.Equal(HttpStatusCode.NotFound, faceMissing.StatusCode);

        const string faceFileName = "face-test.jpg";
        await File.WriteAllBytesAsync(Path.Combine(factory.CapturesDirectory, faceFileName), JpegBytes);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            var stored = await dbContext.DetectionEvents.SingleAsync(e => e.Id == eventId);
            stored.DetectedFaceUri = faceFileName;
            await dbContext.SaveChangesAsync();
        }

        var face = await admin1.GetAsync($"/api/events/{eventId}/face");
        Assert.Equal(HttpStatusCode.OK, face.StatusCode);
        Assert.Equal(JpegBytes, await face.Content.ReadAsByteArrayAsync());

        var crossTenantAnnotated = await admin2.GetAsync($"/api/events/{eventId}/annotated");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantAnnotated.StatusCode);

        var crossTenantFace = await admin2.GetAsync($"/api/events/{eventId}/face");
        Assert.Equal(HttpStatusCode.NotFound, crossTenantFace.StatusCode);
    }

    [Fact]
    public async Task DeleteEvent_WithoutAdminRole_ReturnsForbidden()
    {
        var username = $"user_{Guid.NewGuid():N}"[..20];
        const string password = "regular-user-pass";

        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        var createUser = await admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password,
            role = "User"
        });
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);

        using var userClient = factory.CreateClient();
        var login = await userClient.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password,
            systemId = "sys-001"
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginBody.GetProperty("token").GetString());

        using var piClient = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(JpegBytes);
        var createResponse = await piClient.PostAsync("/api/events", form);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var eventId = created.GetProperty("event").GetProperty("id").GetInt32();

        var response = await userClient.DeleteAsync($"/api/events/{eventId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private HttpClient CreateClient(string? apiKey)
    {
        var client = factory.CreateClient();

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
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

    private static DetectionEvent NewEvent(DateTimeOffset timestamp, bool isSuspect) => new()
    {
        SystemId = "sys-001",
        Timestamp = timestamp,
        IsSuspect = isSuspect,
        Confidence = 0.9f,
        IsOfflineSync = false,
        IsEmailSent = false
    };

    private static MultipartFormDataContent CreateForm(
        byte[]? image,
        DateTimeOffset? capturedAt = null,
        bool isOfflineSync = false,
        Guid? clientEventId = null)
    {
        var form = new MultipartFormDataContent();

        if (image is not null)
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "image", "frame.jpg");
        }

        if (capturedAt is not null)
        {
            form.Add(
                new StringContent(capturedAt.Value.ToString("O", CultureInfo.InvariantCulture)),
                "capturedAt");
        }

        if (clientEventId is not null)
        {
            form.Add(new StringContent(clientEventId.Value.ToString()), "clientEventId");
        }

        form.Add(new StringContent(isOfflineSync ? "true" : "false"), "isOfflineSync");

        return form;
    }
}
