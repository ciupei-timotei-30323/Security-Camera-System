using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Security_Camera_Backend.Contracts;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AwayModeEndpointsTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task Put_NewShape_PersistsScheduleAndReturnsArmedState()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        try
        {
            var response = await admin.PutAsJsonAsync("/api/settings/away-mode", new
            {
                mode = "scheduled",
                startTime = "22:00",
                endTime = "06:00",
                alertEmail = "alerts@example.com",
                timeZone = "Europe/Bucharest"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<AwayModeSettingResponse>();
            Assert.NotNull(body);
            Assert.Equal("scheduled", body!.Mode);
            Assert.Equal("22:00", body.StartTime);
            Assert.Equal("06:00", body.EndTime);
            Assert.Equal("alerts@example.com", body.AlertEmail);
            Assert.Equal("Europe/Bucharest", body.TimeZone);
            Assert.True(body.IsArmed);
            Assert.Equal("sys-001", body.SystemId);

            var get = await admin.GetFromJsonAsync<AwayModeSettingResponse>("/api/settings/away-mode");
            Assert.NotNull(get);
            Assert.Equal("scheduled", get!.Mode);
            Assert.Equal(body.IsArmedNow, get.IsArmedNow);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/settings/away-mode", new { mode = "indefinite" });
        }
    }

    [Fact]
    public async Task Put_LegacyShape_StillWorks()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        try
        {
            var response = await admin.PutAsJsonAsync("/api/away-mode-settings", new { isArmed = false });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<AwayModeSettingResponse>();
            Assert.NotNull(body);
            Assert.Equal("off", body!.Mode);
            Assert.False(body.IsArmed);
            Assert.False(body.IsArmedNow);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/away-mode-settings", new { isArmed = true });
        }
    }

    [Theory]
    [InlineData("""{"mode":"sometimes"}""")]
    [InlineData("""{"mode":"scheduled","startTime":"25:00","endTime":"06:00","timeZone":"UTC"}""")]
    [InlineData("""{"mode":"scheduled","startTime":"22:00","endTime":"06:00","timeZone":"Not/AZone"}""")]
    [InlineData("""{"mode":"scheduled","startTime":"22:00","timeZone":"UTC"}""")]
    [InlineData("""{"mode":"indefinite","alertEmail":"not-an-email"}""")]
    [InlineData("""{}""")]
    public async Task Put_InvalidInput_ReturnsBadRequest(string json)
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync("/api/settings/away-mode", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SuspectEvaluation_UsesEventTimestamp_NotServerNow()
    {
        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password);

        // Pick a capturedAt whose local (UTC) hour is 12 hours away from the current hour, so a
        // one-hour schedule around the event cannot also contain "now".
        var eventTime = DateTimeOffset.UtcNow.AddHours(12);
        eventTime = new DateTimeOffset(
            eventTime.Ticks - eventTime.Ticks % TimeSpan.TicksPerMinute,
            TimeSpan.Zero);

        var startHour = eventTime.UtcDateTime.Hour;
        var endHour = (startHour + 1) % 24;

        try
        {
            var put = await admin.PutAsJsonAsync("/api/settings/away-mode", new
            {
                mode = "scheduled",
                startTime = $"{startHour:D2}:00",
                endTime = $"{endHour:D2}:00",
                timeZone = "UTC"
            });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            using var piClient = factory.CreateClient();
            piClient.DefaultRequestHeaders.Add("X-Api-Key", IntegrationTestFactory.PiApiKey);

            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(JpegBytes);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(image, "image", "frame.jpg");
            form.Add(
                new StringContent(eventTime.ToString("O", CultureInfo.InvariantCulture)),
                "capturedAt");
            form.Add(new StringContent("true"), "isOfflineSync");

            var response = await piClient.PostAsync("/api/events", form);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var detectionEvent = body.GetProperty("event");

            Assert.Equal(eventTime.ToUnixTimeMilliseconds(),
                detectionEvent.GetProperty("timestamp").GetDateTimeOffset().ToUnixTimeMilliseconds());
            Assert.True(detectionEvent.GetProperty("isSuspect").GetBoolean());
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/settings/away-mode", new { mode = "indefinite" });
        }
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
