using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

/// <summary>
/// Extension hook for outbound alerts (ESP32 buzzer, SMTP email, ...).
/// Invoked after a suspect event has been persisted.
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchSuspectAlertAsync(DetectionEvent detectionEvent, CancellationToken ct = default);
}
