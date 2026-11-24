using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Domain-level facade over the MQTT client. Controllers and the detection pipeline call this
/// instead of building topics and payloads themselves, so the wire format lives in one place.
/// </summary>
public interface IRealtimePublisher
{
    /// <summary>
    /// Publishes the retained arm/alarm state for a system (same schema as
    /// <c>GET /api/device/status</c>) and, when the alarm is active, schedules the automatic
    /// "cleared" message for <c>alarmUntil</c>.
    /// </summary>
    Task PublishAlarmStateAsync(string systemId, CancellationToken ct = default);

    /// <summary>Publishes the non-retained event summary used by dashboards and the terminal.</summary>
    Task PublishEventAsync(DetectionEvent detectionEvent, CancellationToken ct = default);
}
