using Security_Camera_Backend.Services.Mqtt;

namespace Security_Camera_Backend.Services.Notifications;

/// <summary>
/// Publishes the retained alarm document for a suspect event over MQTT. It lives in the queued
/// notification pipeline next to <see cref="EmailNotifier"/>, so a slow or unavailable broker can
/// never delay <c>POST /api/events</c>.
/// </summary>
/// <remarks>
/// The alarm state store is already triggered synchronously by the ingestion path, so terminals
/// polling <c>GET /api/device/status</c> see the alarm immediately. This notifier is what makes the
/// push path (ESP32 over MQTT, dashboards over WebSockets) equally immediate.
/// </remarks>
public sealed class MqttNotifier(
    IRealtimePublisher realtimePublisher,
    ILogger<MqttNotifier> logger) : INotifier
{
    public async Task NotifyAsync(SuspectAlert alert, CancellationToken ct)
    {
        try
        {
            await realtimePublisher.PublishAlarmStateAsync(alert.SystemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RealtimePublisher already swallows broker failures; this is a last-resort guard so a
            // single system's MQTT problem cannot abort the rest of the notifier fan-out.
            logger.LogWarning(ex,
                "MQTT alarm publish failed for suspect event {EventId} in system {SystemId}.",
                alert.EventId, alert.SystemId);
        }
    }
}
