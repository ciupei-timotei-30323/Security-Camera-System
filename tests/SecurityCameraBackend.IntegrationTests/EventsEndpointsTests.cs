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
        var capturedAt = DateTimeOffset.UtcNow;
        capturedAt = new DateTimeOffset(
            capturedAt.Ticks - capturedAt.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        int? previousMaxId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();
            previousMaxId = await dbContext.DetectionEvents.MaxAsync(e => (int?)e.Id);
        }

        using var client = CreateClient(IntegrationTestFactory.PiApiKey);
        using var form = CreateForm(JpegBytes, capturedAt);

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

        Assert.Equal(capturedAt.ToUnixTimeMilliseconds(), detectionEvent.Timestamp.ToUnixTimeMilliseconds());
        Assert.True(detectionEvent.IsSuspect);
        Assert.False(detectionEvent.IsOfflineSync);
        Assert.False(detectionEvent.IsEmailSent);
        Assert.Equal(0.95f, detectionEvent.Confidence);
        Assert.Null(detectionEvent.DetectedFaceUri);
        Assert.NotNull(detectionEvent.FullImageUri);
        Assert.StartsWith("/captures/raw_", detectionEvent.FullImageUri);
        Assert.EndsWith(".jpg", detectionEvent.FullImageUri);

        var savedImagePath = Path.Combine(
            factory.CapturesDirectory,
            Path.GetFileName(detectionEvent.FullImageUri!));

        Assert.True(File.Exists(savedImagePath), $"Expected the raw frame at '{savedImagePath}'.");
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(savedImagePath));
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

    private static MultipartFormDataContent CreateForm(byte[]? image, DateTimeOffset? capturedAt = null)
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

        form.Add(new StringContent("false"), "isOfflineSync");

        return form;
    }
}
