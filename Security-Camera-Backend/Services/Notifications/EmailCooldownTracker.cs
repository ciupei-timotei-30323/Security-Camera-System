namespace Security_Camera_Backend.Services.Notifications;

/// <summary>
/// Per-system e-mail cooldown. Counts events suppressed inside the window so the next
/// delivered e-mail can summarize them.
/// </summary>
public sealed class EmailCooldownTracker(TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CooldownState> _states = new(StringComparer.Ordinal);

    public CooldownDecision TryBegin(string systemId, TimeSpan cooldown)
    {
        if (cooldown < TimeSpan.Zero)
        {
            cooldown = TimeSpan.Zero;
        }

        var now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_states.TryGetValue(systemId, out var state) &&
                now - state.LastAttemptAt < cooldown)
            {
                state.SuppressedCount++;
                return CooldownDecision.Suppressed();
            }

            var suppressed = state?.SuppressedCount ?? 0;
            _states[systemId] = new CooldownState(now);

            return CooldownDecision.Send(suppressed);
        }
    }

    private sealed class CooldownState(DateTimeOffset lastAttemptAt)
    {
        public DateTimeOffset LastAttemptAt { get; } = lastAttemptAt;

        public int SuppressedCount { get; set; }
    }
}

public readonly record struct CooldownDecision(bool ShouldSend, int SuppressedCount)
{
    public static CooldownDecision Suppressed() => new(false, 0);

    public static CooldownDecision Send(int suppressedCount) => new(true, suppressedCount);
}
