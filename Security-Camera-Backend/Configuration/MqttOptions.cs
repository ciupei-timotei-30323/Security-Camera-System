namespace Security_Camera_Backend.Configuration;

/// <summary>
/// MQTT broker connection used by the event-driven alert path.
/// Bound from <c>Mqtt</c> (env vars: <c>Mqtt__Host</c>, <c>Mqtt__Port</c>, <c>Mqtt__Username</c>,
/// <c>Mqtt__Password</c>, <c>Mqtt__Enabled</c>).
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is false the publisher is a no-op. That keeps unit/integration test
/// hosts and air-gapped deployments working without a broker, while every other code path
/// (notifier, alarm state, MediaMTX webhook) stays identical.
/// </remarks>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>Enables the publisher. Defaults to true so production fails loudly when misconfigured.</summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "mosquitto";

    public int Port { get; set; } = 1883;

    /// <summary>Broker username. The backend user owns read/write on every <c>systems/#</c> topic.</summary>
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Client identifier; unique per backend instance so the broker does not evict sessions.</summary>
    public string ClientId { get; set; } = "security-camera-backend";

    /// <summary>Root topic prefix. Topic layout is <c>{prefix}/{systemId}/alarm|events</c>.</summary>
    public string TopicPrefix { get; set; } = "systems";

    /// <summary>Seconds the retained alarm message is kept for late subscribers.</summary>
    public int PublishTimeoutSeconds { get; set; } = 5;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Host);
}
