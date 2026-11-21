using System.Collections.Concurrent;
using Security_Camera_Backend.Services.Notifications;

namespace SecurityCameraBackend.IntegrationTests;

/// <summary>Captures outbound e-mail in memory and lets tests wait for background delivery.</summary>
public sealed class FakeSmtpSender : ISmtpSender
{
    private readonly ConcurrentQueue<OutboundEmail> _emails = new();

    public IReadOnlyList<OutboundEmail> Emails => _emails.ToArray();

    public Task SendAsync(OutboundEmail email, CancellationToken ct)
    {
        _emails.Enqueue(email);
        return Task.CompletedTask;
    }

    public async Task<OutboundEmail> WaitForAsync(
        Func<OutboundEmail, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            var match = Emails.FirstOrDefault(predicate);

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Expected outbound e-mail was not sent in time.");
    }
}
