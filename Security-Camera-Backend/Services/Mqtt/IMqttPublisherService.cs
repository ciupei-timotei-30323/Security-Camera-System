namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Narrow publisher surface. Keeping it an interface lets integration tests swap in a recording
/// fake without a broker, and lets the notifier/MediaMTX code depend on 2 methods instead of
/// MQTTnet's client API.
/// </summary>
public interface IMqttPublisherService
{
    /// <summary>
    /// Publishes a JSON payload at QoS 1. <paramref name="retain"/> keeps the message on the broker
    /// so a terminal that reconnects instantly gets the current state instead of waiting for the
    /// next event.
    /// </summary>
    /// <returns>True when the message was handed to a connected broker.</returns>
    Task<bool> PublishJsonAsync(
        string topic,
        string payload,
        bool retain,
        CancellationToken ct = default);
}
