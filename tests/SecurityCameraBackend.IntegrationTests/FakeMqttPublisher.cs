using System.Collections.Concurrent;
using System.Text.Json;
using Security_Camera_Backend.Services.Mqtt;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>One captured <c>IMqttPublisherService.PublishJsonAsync</c> call.</summary>
public sealed record PublishedMqttMessage(string Topic, string Payload, bool Retain)
{
    public JsonElement Json() => JsonDocument.Parse(Payload).RootElement;
}

/// <summary>
/// Records MQTT publishes in memory so the event-driven alert path can be asserted without a
/// broker. The real <see cref="MqttPublisherService"/> is replaced with this fake in
/// <see cref="IntegrationTestFactory.ConfigureWebHost"/>, so nothing in the suite opens a socket.
/// </summary>
public sealed class FakeMqttPublisher : IMqttPublisherService
{
    private readonly ConcurrentQueue<PublishedMqttMessage> _messages = new();

    public IReadOnlyList<PublishedMqttMessage> Messages => _messages.ToArray();

    public Task<bool> PublishJsonAsync(
        string topic,
        string payload,
        bool retain,
        CancellationToken ct = default)
    {
        _messages.Enqueue(new PublishedMqttMessage(topic, payload, retain));
        return Task.FromResult(true);
    }

    /// <summary>Waits for a message matching the predicate, or throws after the timeout.</summary>
    public async Task<PublishedMqttMessage> WaitForAsync(
        Func<PublishedMqttMessage, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            var match = Messages.FirstOrDefault(predicate);

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            "Expected MQTT message was not published in time. Published topics: " +
            string.Join(", ", Messages.Select(m => m.Topic)));
    }

    /// <summary>Waits for a message on <paramref name="topic"/> whose JSON satisfies the predicate.</summary>
    public Task<PublishedMqttMessage> WaitForJsonAsync(
        string topic,
        Func<JsonElement, bool> predicate,
        TimeSpan? timeout = null) =>
        WaitForAsync(
            message => message.Topic == topic && predicate(message.Json()),
            timeout);
}
