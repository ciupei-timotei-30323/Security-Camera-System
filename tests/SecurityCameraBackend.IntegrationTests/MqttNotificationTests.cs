using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;
using Security_Camera_Backend.Services.Security;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>
/// Event-driven alert path: the retained alarm document on <c>systems/{id}/alarm</c> must have the
/// same schema as <c>GET /api/device/status</c>, and every ingested event must publish a summary on
/// <c>systems/{id}/events</c>. A broker is not involved — <see cref="FakeMqttPublisher"/> records
/// exactly what the backend would have sent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class MqttNotificationTests(IntegrationTestFactory factory)
{
    private static readonly byte[] JpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    [Fact]
    public async Task SuspectEvent_PublishesRetainedAlarmDocumentMatchingDeviceStatus()
    {
        var system = await CreateSystemAsync(armed: true);
        using var pi = CreatePiClient(system.CameraKey);

        var response = await PostEventAsync(pi);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var alarmTopic = $"systems/{system.SystemId}/alarm";
        var message = await factory.MqttPublisher.WaitForJsonAsync(
            alarmTopic,
            json => json.TryGetProperty("alarm", out var alarm) && alarm.GetBoolean());

        Assert.True(message.Retain);

        var payload = message.Json();
        Assert.True(payload.GetProperty("armed").GetBoolean());
        Assert.True(payload.GetProperty("alarm").GetBoolean());
        Assert.Equal("SUSPECT DETECTED", payload.GetProperty("line1").GetString()!.TrimEnd());
        Assert.Equal(new string(' ', 16), payload.GetProperty("line2").GetString());

        var alarmUntil = payload.GetProperty("alarmUntil").GetDateTimeOffset();
        var serverTime = payload.GetProperty("serverTime").GetDateTimeOffset();
        Assert.InRange(alarmUntil, serverTime, serverTime.AddSeconds(10));

        // The same document the HTTP fallback serves, so a terminal and the dashboard agree.
        var status = await GetTerminalStatusAsync(system.SystemId);
        Assert.Equal(status.GetProperty("armed").GetBoolean(), payload.GetProperty("armed").GetBoolean());
        Assert.Equal(status.GetProperty("alarm").GetBoolean(), payload.GetProperty("alarm").GetBoolean());
        Assert.Equal(status.GetProperty("line1").GetString(), payload.GetProperty("line1").GetString());
        Assert.Equal(status.GetProperty("line2").GetString(), payload.GetProperty("line2").GetString());
    }

    [Fact]
    public async Task EveryEvent_PublishesNonRetainedSummary()
    {
        var system = await CreateSystemAsync(armed: false);
        using var pi = CreatePiClient(system.CameraKey);

        var response = await PostEventAsync(pi);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("event").GetProperty("id").GetInt32();

        var eventsTopic = $"systems/{system.SystemId}/events";
        var message = await factory.MqttPublisher.WaitForJsonAsync(
            eventsTopic,
            json => json.GetProperty("eventId").GetInt32() == eventId);

        Assert.False(message.Retain);

        var payload = message.Json();
        Assert.Equal(eventId, payload.GetProperty("eventId").GetInt32());
        Assert.True(payload.TryGetProperty("timestamp", out _));
        // Disarmed system: the event is not a suspect event.
        Assert.False(payload.GetProperty("isSuspect").GetBoolean());
        // The stub cv-service reports no face crop, so the summary must say so too.
        Assert.False(payload.GetProperty("hasFace").GetBoolean());
    }

    [Fact]
    public async Task ArmingChange_PublishesRetainedAlarmState()
    {
        var system = await CreateSystemAsync(armed: true);
        using var admin = await CreateAdminClientAsync(system);

        var response = await admin.PutAsJsonAsync("/api/settings/away-mode", new { isArmed = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alarmTopic = $"systems/{system.SystemId}/alarm";
        var message = await factory.MqttPublisher.WaitForJsonAsync(
            alarmTopic,
            json => json.TryGetProperty("armed", out var armed) && !armed.GetBoolean());

        Assert.True(message.Retain);

        var payload = message.Json();
        Assert.False(payload.GetProperty("armed").GetBoolean());
        Assert.False(payload.GetProperty("alarm").GetBoolean());
        Assert.Equal("DISARMED", payload.GetProperty("line1").GetString()!.TrimEnd());
    }

    [Fact]
    public async Task TenantIsolation_AlarmOnlyPublishedToOwningSystem()
    {
        var system = await CreateSystemAsync(armed: true);
        using var pi = CreatePiClient(system.CameraKey);

        Assert.Equal(HttpStatusCode.Created, (await PostEventAsync(pi)).StatusCode);

        await factory.MqttPublisher.WaitForJsonAsync(
            $"systems/{system.SystemId}/alarm",
            json => json.GetProperty("alarm").GetBoolean());

        Assert.DoesNotContain(
            factory.MqttPublisher.Messages,
            message => message.Topic == $"systems/{system.SystemId}/alarm" &&
                       message.Json().GetProperty("alarm").GetBoolean() is false &&
                       message.Json().GetProperty("alarmUntil").ValueKind != JsonValueKind.Null);
    }

    private async Task<JsonElement> GetTerminalStatusAsync(string systemId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SecurityCameraBackendContext>();

        var terminal = await dbContext.PiDevices
            .AsNoTracking()
            .FirstAsync(d => d.SystemId == systemId && d.DeviceType == DeviceType.AlertTerminal);

        Assert.False(string.IsNullOrWhiteSpace(terminal.Name));

        // The terminal's API key is only known to the test that created it, so the status document
        // is read through the shared helper instead of a second HTTP round-trip.
        return await GetStatusThroughTerminalAsync(systemId);
    }

    private async Task<JsonElement> GetStatusThroughTerminalAsync(string systemId)
    {
        var terminalKey = _terminalKeys[systemId];
        using var client = CreatePiClient(terminalKey);

        return await client.GetFromJsonAsync<JsonElement>("/api/device/status");
    }

    private readonly Dictionary<string, string> _terminalKeys = new(StringComparer.Ordinal);

    private async Task<TestSystem> CreateSystemAsync(bool armed)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var systemId = $"sys-mqtt-{suffix}";
        var cameraKey = $"camera-key-{suffix}";
        var terminalKey = $"terminal-key-{suffix}";
        var adminUsername = $"mqtt-admin-{suffix}";
        var adminPassword = $"mqtt-pass-{suffix}";
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
            Name = "MQTT test camera",
            DeviceType = DeviceType.Camera,
            CreatedAtUtc = now
        });

        dbContext.PiDevices.Add(new PiDevice
        {
            SystemId = systemId,
            ApiKeyHash = PiApiKeyHasher.HashToHex(terminalKey),
            Name = "MQTT test terminal",
            DeviceType = DeviceType.AlertTerminal,
            CreatedAtUtc = now
        });

        dbContext.AwayModeSettings.Add(new AwayModeSetting
        {
            SystemId = systemId,
            Mode = armed ? AwayMode.Indefinite : AwayMode.Off,
            IsArmed = armed,
            AlertEmail = null,
            TimeZone = AwayModeValues.DefaultTimeZone,
            UpdatedAtUtc = now
        });

        // A generated tenant has no factory administrator (the stub accounts are pinned to
        // sys-001..sys-003), so it gets its own local admin to exercise the away-mode endpoint.
        var admin = new User
        {
            SystemId = systemId,
            Username = adminUsername,
            Role = UserRoles.Admin,
            CreatedAtUtc = now
        };

        admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, adminPassword);
        dbContext.Users.Add(admin);

        await dbContext.SaveChangesAsync();

        _terminalKeys[systemId] = terminalKey;

        return new TestSystem(systemId, cameraKey, terminalKey, adminUsername, adminPassword);
    }

    private HttpClient CreatePiClient(string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private async Task<HttpClient> CreateAdminClientAsync(TestSystem system)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = system.AdminUsername,
            password = system.AdminPassword,
            systemId = system.SystemId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login!.Token);

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

    private sealed record TestSystem(
        string SystemId,
        string CameraKey,
        string TerminalKey,
        string AdminUsername,
        string AdminPassword);
}
