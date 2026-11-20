using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services;

/// <summary>Default no-op dispatcher until ESP32/email alerting is wired in.</summary>
public sealed class NoOpNotificationDispatcher : INotificationDispatcher
{
    public Task DispatchSuspectAlertAsync(DetectionEvent detectionEvent, CancellationToken ct = default) =>
        Task.CompletedTask;
}
