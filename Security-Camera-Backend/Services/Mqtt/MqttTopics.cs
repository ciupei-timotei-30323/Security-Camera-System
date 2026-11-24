namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Topic layout shared by the backend, the ESP32 firmware and the dashboard.
/// Kept in one place so a rename cannot silently break one of the three consumers.
/// </summary>
public static class MqttTopics
{
    /// <summary>Retained alarm/arm state, schema identical to <c>GET /api/device/status</c>.</summary>
    public static string Alarm(string prefix, string systemId) =>
        $"{Normalize(prefix)}/{RequireSystemId(systemId)}/alarm";

    /// <summary>Non-retained detection summary for every ingested event.</summary>
    public static string Events(string prefix, string systemId) =>
        $"{Normalize(prefix)}/{RequireSystemId(systemId)}/events";

    private static string Normalize(string prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? "systems" : prefix.Trim().Trim('/');

    private static string RequireSystemId(string systemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        return systemId.Trim();
    }
}
