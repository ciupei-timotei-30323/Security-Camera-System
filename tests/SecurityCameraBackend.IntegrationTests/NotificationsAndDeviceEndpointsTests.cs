using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Security;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class NotificationsAndDeviceEndpointsTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task SuspectEvent_SendsOneEmailWithDashboardLinkAndAttachment()
    {
        var recipient = $"suspect-{Guid.NewGuid():N}@test.local";
        var system = await CreateSystemAsync(recipient, armed: true, includeTerminal: false);

        using var pi = CreatePiClient(system.CameraKey);
        var response = await PostEventAsync(pi);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("event").GetProperty("id").GetInt32();

        var email = await factory.EmailSender.WaitForAsync(message => message.To == recipient);

        Assert.Contains(system.SystemId, email.Subject);
        Assert.Contains(system.SystemId, email.TextBody);
        Assert.Contains("http://dashboard.test", email.HtmlBody);
        Assert.Contains($"/events/{eventId}", email.TextBody);
        Assert.NotNull(email.AttachmentContent);
        Assert.Equal(JpegBytes, email.AttachmentContent);
        Assert.EndsWith(".jpg", email.AttachmentFileName);
        Assert.Single(factory.EmailSender.Emails, message => message.To == recipient);
    }

    [Fact]
    public async Task NonSuspectEvent_DoesNotSendEmail()
    {
        var recipient = $"quiet-{Guid.NewGuid():N}@test.local";
        var system = await CreateSystemAsync(recipient, armed: false, includeTerminal: false);

        using var pi = CreatePiClient(system.CameraKey);
        var response = await PostEventAsync(pi);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await Task.Delay(750);

        Assert.DoesNotContain(
            factory.EmailSender.Emails,
            message => message.To == recipient);
    }

    [Fact]
    public async Task EmailCooldown_SuppressesAndSummarizesEvents()
    {
        var recipient = $"cooldown-{Guid.NewGuid():N}@test.local";
        var system = await CreateSystemAsync(recipient, armed: true, includeTerminal: false);

        using var pi = CreatePiClient(system.CameraKey);

        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(pi)).StatusCode);
        await factory.EmailSender.WaitForAsync(message => message.To == recipient);

        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(pi)).StatusCode);
        await Task.Delay(400);
        Assert.Single(factory.EmailSender.Emails, message => message.To == recipient);

        await Task.Delay(1_100);

        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(pi)).StatusCode);
        var summaryEmail = await factory.EmailSender.WaitForAsync(
            message => message.To == recipient &&
                       message.TextBody.Contains("additional suspect event"));

        Assert.Contains("1 additional suspect event(s)", summaryEmail.TextBody);
        Assert.Equal(2, factory.EmailSender.Emails.Count(message => message.To == recipient));
    }

    [Fact]
    public async Task DeviceStatus_IsTerminalOnly_AndAlarmWindowIsTenantScoped()
    {
        var systemA = await CreateSystemAsync(
            alertEmail: null,
            armed: true,
            includeTerminal: true);
        var systemB = await CreateSystemAsync(
            alertEmail: null,
            armed: true,
            includeTerminal: true);

        using var cameraA = CreatePiClient(systemA.CameraKey);
        using var terminalA = CreatePiClient(systemA.TerminalKey!);
        using var terminalB = CreatePiClient(systemB.TerminalKey!);

        var cameraUsingDeviceEndpoint = await cameraA.GetAsync("/api/device/status");
        Assert.Equal(HttpStatusCode.Unauthorized, cameraUsingDeviceEndpoint.StatusCode);

        var idleB = await terminalB.GetFromJsonAsync<JsonElement>("/api/device/status");
        Assert.True(idleB.GetProperty("armed").GetBoolean());
        Assert.False(idleB.GetProperty("alarm").GetBoolean());
        Assert.Equal(Padded("ARMED"), idleB.GetProperty("line1").GetString());
        Assert.Equal(new string(' ', 16), idleB.GetProperty("line2").GetString());

        var eventResponse = await PostEventAsync(cameraA);
        Assert.Equal(HttpStatusCode.Created, eventResponse.StatusCode);

        var alarmA = await terminalA.GetFromJsonAsync<JsonElement>("/api/device/status");
        Assert.True(alarmA.GetProperty("armed").GetBoolean());
        Assert.True(alarmA.GetProperty("alarm").GetBoolean());
        Assert.Equal(Padded("SUSPECT DETECTED"), alarmA.GetProperty("line1").GetString());

        var alarmUntil = alarmA.GetProperty("alarmUntil").GetDateTimeOffset();
        var serverTime = alarmA.GetProperty("serverTime").GetDateTimeOffset();
        Assert.InRange(alarmUntil, serverTime, serverTime.AddSeconds(5));

        // sys-002 must not see sys-001's alarm.
        var stillIdleB = await terminalB.GetFromJsonAsync<JsonElement>("/api/device/status");
        Assert.False(stillIdleB.GetProperty("alarm").GetBoolean());
        Assert.Equal(Padded("ARMED"), stillIdleB.GetProperty("line1").GetString());

        // Test configuration registers terminal keys the same way as camera keys.
        using var configuredTerminal = CreatePiClient(IntegrationTestFactory.AlertTerminalKeySys001);
        Assert.Equal(HttpStatusCode.OK, (await configuredTerminal.GetAsync("/api/device/status")).StatusCode);

        await WaitForAlarmToExpireAsync(terminalA);
    }

    [Fact]
    public async Task DeviceStatus_RequiresApiKey()
    {
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/device/status")).StatusCode);
    }

    private async Task WaitForAlarmToExpireAsync(HttpClient terminal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            var status = await terminal.GetFromJsonAsync<JsonElement>("/api/device/status");

            if (!status.GetProperty("alarm").GetBoolean())
            {
                Assert.Equal(Padded("ARMED"), status.GetProperty("line1").GetString());
                Assert.Null(status.GetProperty("alarmUntil").GetString());
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Alarm did not expire within the expected window.");
    }

    private async Task<TestSystem> CreateSystemAsync(
        string? alertEmail,
        bool armed,
        bool includeTerminal)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var systemId = $"sys-notify-{suffix}";
        var cameraKey = $"camera-key-{suffix}";
        var terminalKey = includeTerminal ? $"terminal-key-{suffix}" : null;
        var now = DateTime.UtcNow;

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

        dbContext.CameraSystems.Add(new CameraSystem
        {
            SystemId = systemId,
            DisplayName = systemId,
            CreatedAtUtc = now
        });

        dbContext.PiDevices.Add(new PiDevice
        {
            SystemId = systemId,
            ApiKeyHash = PiApiKeyHasher.HashToHex(cameraKey),
            Name = "Test camera",
            DeviceType = DeviceType.Camera,
            CreatedAtUtc = now
        });

        if (terminalKey is not null)
        {
            dbContext.PiDevices.Add(new PiDevice
            {
                SystemId = systemId,
                ApiKeyHash = PiApiKeyHasher.HashToHex(terminalKey),
                Name = "Test terminal",
                DeviceType = DeviceType.AlertTerminal,
                CreatedAtUtc = now
            });
        }

        dbContext.AwayModeSettings.Add(new AwayModeSetting
        {
            SystemId = systemId,
            Mode = armed ? AwayMode.Indefinite : AwayMode.Off,
            IsArmed = armed,
            AlertEmail = alertEmail,
            TimeZone = AwayModeValues.DefaultTimeZone,
            UpdatedAtUtc = now
        });

        await dbContext.SaveChangesAsync();

        return new TestSystem(systemId, cameraKey, terminalKey);
    }

    private HttpClient CreatePiClient(string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static async Task<HttpResponseMessage> PostEventAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(JpegBytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(image, "image", "frame.jpg");

        return await client.PostAsync("/api/events", content);
    }

    private static string Padded(string value) => value.PadRight(16);

    private sealed record TestSystem(string SystemId, string CameraKey, string? TerminalKey);
}
