using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services.Auth;

/// <summary>
/// In-memory failed-login lockout, keyed by <c>username</c> + <c>systemId</c>.
///
/// A per-process tracker is deliberate: it stops password spraying against one account without a
/// database round-trip on every attempt, and it degrades safely (a restart clears the counters, and
/// the IP rate limiter in <c>Program.cs</c> still applies).
/// </summary>
public sealed class LoginLockoutTracker(
    IOptions<LoginLockoutOptions> options,
    TimeProvider timeProvider,
    ILogger<LoginLockoutTracker> logger)
{
    private sealed class FailureState
    {
        public int Failures;
        public long LockedUntilTicks;
    }

    /// <summary>
    /// Upper bound on tracked identities. Without it, an attacker could grow this dictionary
    /// without limit by failing logins for random usernames (memory exhaustion).
    /// </summary>
    private const int MaxTrackedIdentities = 10_000;

    private readonly ConcurrentDictionary<string, FailureState> _states = new(StringComparer.Ordinal);

    private LoginLockoutOptions Options => options.Value;

    /// <summary>True when the identity is currently locked out; <paramref name="retryAfter"/> is how long is left.</summary>
    public bool IsLockedOut(string username, string? systemId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        if (!Options.Enabled || Options.MaxFailures <= 0)
        {
            return false;
        }

        if (!_states.TryGetValue(BuildKey(username, systemId), out var state))
        {
            return false;
        }

        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        lock (state)
        {
            if (state.LockedUntilTicks <= nowTicks)
            {
                return false;
            }

            retryAfter = TimeSpan.FromTicks(state.LockedUntilTicks - nowTicks);
            return true;
        }
    }

    /// <summary>Records one failed attempt and locks the identity once the threshold is reached.</summary>
    public void RegisterFailure(string username, string? systemId)
    {
        if (!Options.Enabled || Options.MaxFailures <= 0)
        {
            return;
        }

        var key = BuildKey(username, systemId);

        if (_states.Count >= MaxTrackedIdentities)
        {
            Prune(keepKey: key);
        }

        var state = _states.GetOrAdd(key, _ => new FailureState());

        lock (state)
        {
            state.Failures++;

            if (state.Failures >= Options.MaxFailures)
            {
                state.LockedUntilTicks = timeProvider
                    .GetUtcNow()
                    .AddSeconds(Math.Max(1, Options.LockoutSeconds))
                    .UtcTicks;
                state.Failures = 0;

                logger.LogWarning(
                    "Locked login attempts for {Username} (system: {SystemId}) for {LockoutSeconds}s after {MaxFailures} consecutive failures.",
                    username, systemId ?? "<any>", Options.LockoutSeconds, Options.MaxFailures);
            }
        }
    }

    /// <summary>Clears the failure counter after a successful login.</summary>
    public void Reset(string username, string? systemId)
    {
        if (_states.TryRemove(BuildKey(username, systemId), out var state))
        {
            lock (state)
            {
                state.Failures = 0;
                state.LockedUntilTicks = 0;
            }
        }
    }

    /// <summary>
    /// Drops identities that are neither locked nor mid-way through counting, so the tracker stays
    /// bounded. If nothing is prunable the accounting is dropped wholesale: forgetting failures is
    /// the safe failure mode, and the per-IP limiter still caps the attempt rate.
    /// </summary>
    private void Prune(string keepKey)
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;

        foreach (var (key, state) in _states)
        {
            if (key == keepKey)
            {
                continue;
            }

            lock (state)
            {
                if (state.LockedUntilTicks <= nowTicks && state.Failures == 0)
                {
                    _states.TryRemove(key, out _);
                }
            }
        }

        if (_states.Count >= MaxTrackedIdentities)
        {
            logger.LogWarning(
                "Login lockout tracker reached {MaxTrackedIdentities} identities; clearing failure counters.",
                MaxTrackedIdentities);

            foreach (var (key, state) in _states)
            {
                if (key == keepKey)
                {
                    continue;
                }

                lock (state)
                {
                    if (state.LockedUntilTicks <= nowTicks)
                    {
                        _states.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    private static string BuildKey(string username, string? systemId) =>
        $"{username.Trim().ToLowerInvariant()}|{systemId?.Trim().ToLowerInvariant() ?? string.Empty}";
}
