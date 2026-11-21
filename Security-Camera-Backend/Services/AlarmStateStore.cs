using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services;

public interface IAlarmStateStore
{
    /// <summary>Starts or restarts the alarm window for a system.</summary>
    void Trigger(string systemId);

    /// <summary>Returns the active alarm end time, or null when the system is quiet.</summary>
    DateTimeOffset? GetActiveUntil(string systemId);
}

/// <summary>Process-local alarm windows. The terminal polls this state for its buzzer/LCD.</summary>
public sealed class AlarmStateStore(
    IOptions<AlarmOptions> alarmOptions,
    TimeProvider timeProvider) : IAlarmStateStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _alarmUntil =
        new(StringComparer.Ordinal);

    public void Trigger(string systemId)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, alarmOptions.Value.DurationSeconds));
        _alarmUntil[systemId] = timeProvider.GetUtcNow().Add(duration);
    }

    public DateTimeOffset? GetActiveUntil(string systemId)
    {
        if (!_alarmUntil.TryGetValue(systemId, out var until))
        {
            return null;
        }

        if (until > timeProvider.GetUtcNow())
        {
            return until;
        }

        _alarmUntil.TryRemove(systemId, out _);
        return null;
    }
}
