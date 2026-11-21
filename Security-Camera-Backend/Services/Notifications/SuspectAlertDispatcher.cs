namespace Security_Camera_Backend.Services.Notifications;

/// <summary>Consumes queued suspect alerts and runs every configured notifier independently.</summary>
public sealed class SuspectAlertDispatcher(
    SuspectAlertQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<SuspectAlertDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var notifiers = scope.ServiceProvider.GetRequiredService<IEnumerable<INotifier>>();

                foreach (var notifier in notifiers)
                {
                    try
                    {
                        await notifier.NotifyAsync(alert, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex,
                            "Notifier {Notifier} failed for suspect event {EventId} in system {SystemId}.",
                            notifier.GetType().Name, alert.EventId, alert.SystemId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Suspect alert dispatch failed for event {EventId} in system {SystemId}.",
                    alert.EventId, alert.SystemId);
            }
        }
    }
}
