using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Builds and publishes the MQTT documents for one system.
///
/// Two message families:
/// <list type="bullet">
///   <item><c>systems/{systemId}/alarm</c> — retained, QoS 1, schema identical to
///   <c>GET /api/device/status</c>. The retained flag means a terminal or dashboard that
///   reconnects immediately learns the current arm/alarm state.</item>
///   <item><c>systems/{systemId}/events</c> — non-retained detection summary, so open dashboards
///   refresh without polling.</item>
/// </list>
///
/// The singleton owns the "alarm cleared" timers. A newer alarm for the same system cancels the
/// pending timer, so a short burst of detections cannot clear the alarm early.
/// </summary>
public sealed class RealtimePublisher : IRealtimePublisher, IDisposable
{
    /// <summary>Extra delay after <c>alarmUntil</c> so the broker never publishes "cleared" early.</summary>
    private static readonly TimeSpan ClearGrace = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMqttPublisherService _mqtt;
    private readonly MqttOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RealtimePublisher> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _clearTimers =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public RealtimePublisher(
        IServiceScopeFactory scopeFactory,
        IMqttPublisherService mqtt,
        IOptions<MqttOptions> options,
        TimeProvider timeProvider,
        ILogger<RealtimePublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _mqtt = mqtt;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task PublishAlarmStateAsync(string systemId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return;
        }

        var snapshot = await ReadSnapshotAsync(systemId, ct);

        if (snapshot is null)
        {
            return;
        }

        await PublishAlarmSnapshotAsync(systemId, snapshot, ct);

        if (snapshot.AlarmUntil is not null)
        {
            ScheduleClear(systemId, snapshot.AlarmUntil.Value);
        }
    }

    public async Task PublishEventAsync(DetectionEvent detectionEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(detectionEvent);

        if (!_options.IsConfigured)
        {
            return;
        }

        var message = new EventNotificationMessage(
            detectionEvent.Id,
            detectionEvent.Timestamp,
            detectionEvent.IsSuspect,
            detectionEvent.DetectedFaceUri is not null);

        var topic = MqttTopics.Events(_options.TopicPrefix, detectionEvent.SystemId);

        try
        {
            await _mqtt.PublishJsonAsync(topic, MqttJson.Serialize(message), retain: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish event {EventId} summary to {Topic}.",
                detectionEvent.Id,
                topic);
        }
    }

    /// <summary>
    /// Reads the current arm/alarm state. Returns null when the system row cannot be resolved, in
    /// which case nothing is published (the HTTP endpoint stays authoritative).
    /// </summary>
    private async Task<DeviceTerminalStatusResponse?> ReadSnapshotAsync(
        string systemId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var systemStateService = scope.ServiceProvider.GetRequiredService<ISystemStateService>();
        var alarmStateStore = scope.ServiceProvider.GetRequiredService<IAlarmStateStore>();

        var now = _timeProvider.GetUtcNow();
        var armed = await systemStateService.IsArmedAsync(systemId, now, ct);
        var alarmUntil = alarmStateStore.GetActiveUntil(systemId);
        var alarm = alarmUntil is not null;

        var line1 = alarm
            ? "SUSPECT DETECTED"
            : armed
                ? "ARMED"
                : "DISARMED";

        return new DeviceTerminalStatusResponse(
            armed,
            alarm,
            alarmUntil,
            TerminalDisplay.Line(line1),
            TerminalDisplay.BlankLine(),
            now);
    }

    private async Task PublishAlarmSnapshotAsync(
        string systemId,
        DeviceTerminalStatusResponse snapshot,
        CancellationToken ct)
    {
        var topic = MqttTopics.Alarm(_options.TopicPrefix, systemId);

        try
        {
            await _mqtt.PublishJsonAsync(topic, MqttJson.Serialize(snapshot), retain: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to publish alarm state to {Topic}.", topic);
        }
    }

    /// <summary>
    /// Publishes the cleared state once <paramref name="alarmUntil"/> passes. A newer alarm
    /// replaces the timer, so an old timer can never shorten a newer window.
    /// </summary>
    private void ScheduleClear(string systemId, DateTimeOffset alarmUntil)
    {
        if (_disposed)
        {
            return;
        }

        var delay = alarmUntil - _timeProvider.GetUtcNow() + ClearGrace;

        if (delay < TimeSpan.Zero)
        {
            delay = ClearGrace;
        }

        var cts = new CancellationTokenSource();

        _clearTimers.AddOrUpdate(
            systemId,
            _ => cts,
            (_, previous) =>
            {
                previous.Cancel();
                previous.Dispose();
                return cts;
            });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var alarmStateStore = scope.ServiceProvider.GetRequiredService<IAlarmStateStore>();

                // A newer alarm may have extended the window after this timer was scheduled.
                var activeUntil = alarmStateStore.GetActiveUntil(systemId);

                if (activeUntil is not null && activeUntil > _timeProvider.GetUtcNow())
                {
                    return;
                }

                var snapshot = await ReadSnapshotAsync(systemId, cts.Token);

                if (snapshot is null)
                {
                    return;
                }

                await PublishAlarmSnapshotAsync(systemId, snapshot, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer alarm or shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish cleared alarm state for system {SystemId}.", systemId);
            }
            finally
            {
                _clearTimers.TryRemove(new KeyValuePair<string, CancellationTokenSource>(systemId, cts));
                cts.Dispose();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var cts in _clearTimers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _clearTimers.Clear();
    }
}
