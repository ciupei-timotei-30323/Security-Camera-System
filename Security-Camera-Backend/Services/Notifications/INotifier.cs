namespace Security_Camera_Backend.Services.Notifications;

/// <summary>One outbound side effect for a suspect event.</summary>
public interface INotifier
{
    Task NotifyAsync(SuspectAlert alert, CancellationToken ct);
}
