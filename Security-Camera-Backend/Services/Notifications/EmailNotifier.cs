using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Models;

namespace Security_Camera_Backend.Services.Notifications;

/// <summary>
/// Sends suspect-event e-mails through SMTP. Cooldown state is process-local, matching the
/// in-memory alert queue and terminal alarm state.
/// </summary>
public sealed class EmailNotifier(
    SecurityCameraBackendContext dbContext,
    ICaptureStorage captureStorage,
    ISmtpSender smtpSender,
    EmailCooldownTracker cooldownTracker,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<EmailNotifier> logger) : INotifier
{
    private const int MaxSendAttempts = 4; // one initial attempt + three retries

    public async Task NotifyAsync(SuspectAlert alert, CancellationToken ct)
    {
        var options = notificationOptions.Value;

        var alertEmail = await dbContext.AwayModeSettings
            .AsNoTracking()
            .Where(setting => setting.SystemId == alert.SystemId)
            .Select(setting => setting.AlertEmail)
            .SingleOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(alertEmail))
        {
            logger.LogDebug(
                "No AlertEmail configured for system {SystemId}; no e-mail sent for event {EventId}.",
                alert.SystemId, alert.EventId);
            return;
        }

        var decision = cooldownTracker.TryBegin(
            alert.SystemId,
            TimeSpan.FromSeconds(Math.Max(0, options.EmailCooldownSeconds)));

        if (!decision.ShouldSend)
        {
            logger.LogInformation(
                "Suspect alert for system {SystemId} suppressed by the e-mail cooldown.",
                alert.SystemId);
            return;
        }

        var email = BuildEmail(alert, alertEmail, options.DashboardBaseUrl, decision.SuppressedCount);

        await SendWithRetriesAsync(email, alert, options, ct);

        logger.LogInformation(
            "Sent suspect alert e-mail for event {EventId} in system {SystemId}.",
            alert.EventId, alert.SystemId);
    }

    private async Task SendWithRetriesAsync(
        OutboundEmail email,
        SuspectAlert alert,
        NotificationOptions options,
        CancellationToken ct)
    {
        var baseDelay = TimeSpan.FromMilliseconds(Math.Max(1, options.EmailRetryBaseDelayMilliseconds));

        for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
        {
            try
            {
                await smtpSender.SendAsync(email, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxSendAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

                logger.LogWarning(ex,
                    "Sending alert e-mail for event {EventId} failed (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}.",
                    alert.EventId, attempt, MaxSendAttempts, delay);

                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Sending alert e-mail for event {EventId} failed after {MaxAttempts} attempts.",
                    alert.EventId, MaxSendAttempts);
                throw;
            }
        }
    }

    private OutboundEmail BuildEmail(
        SuspectAlert alert,
        string recipient,
        string dashboardBaseUrl,
        int suppressedCount)
    {
        var when = alert.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        var dashboardUrl = $"{dashboardBaseUrl.TrimEnd('/')}/events/{alert.EventId}";
        var subject = $"[Security Camera] Suspect detected at {when} ({alert.SystemId})";

        var summary = suppressedCount > 0
            ? $"\n\n{suppressedCount} additional suspect event(s) occurred during the cooldown window and are summarized by this alert.\n"
            : string.Empty;

        var textBody =
            $"Suspect activity was detected.\n\n" +
            $"Time: {when}\n" +
            $"System: {alert.SystemId}\n" +
            $"Event: {alert.EventId}\n" +
            $"Confidence: {FormatConfidence(alert.Confidence)}\n" +
            summary +
            $"\nOpen the dashboard: {dashboardUrl}\n";

        var htmlSummary = suppressedCount > 0
            ? $"<p><strong>{suppressedCount} additional suspect event(s)</strong> occurred during the cooldown window and are summarized by this alert.</p>"
            : string.Empty;

        var htmlBody =
            $"<p>Suspect activity was detected.</p>" +
            "<ul>" +
            $"<li><strong>Time:</strong> {WebUtility.HtmlEncode(when)}</li>" +
            $"<li><strong>System:</strong> {WebUtility.HtmlEncode(alert.SystemId)}</li>" +
            $"<li><strong>Event:</strong> {alert.EventId}</li>" +
            $"<li><strong>Confidence:</strong> {WebUtility.HtmlEncode(FormatConfidence(alert.Confidence))}</li>" +
            "</ul>" +
            htmlSummary +
            $"<p><a href=\"{WebUtility.HtmlEncode(dashboardUrl)}\">Open the dashboard</a></p>";

        var attachmentPath = ResolveAttachmentPath(alert);

        return new OutboundEmail(
            recipient,
            subject,
            textBody,
            htmlBody,
            attachmentPath is null ? null : Path.GetFileName(attachmentPath),
            attachmentPath is null ? null : File.ReadAllBytes(attachmentPath),
            attachmentPath is null ? "image/jpeg" : GetContentType(attachmentPath));
    }

    private string? ResolveAttachmentPath(SuspectAlert alert) =>
        captureStorage.TryResolveExistingCapturePath(alert.DetectedFaceUri) ??
        captureStorage.TryResolveExistingCapturePath(alert.AnnotatedImageUri) ??
        captureStorage.TryResolveExistingCapturePath(alert.FullImageUri);

    private static string FormatConfidence(float? confidence) =>
        confidence is null ? "n/a" : $"{confidence.Value:P0}";

    private static string GetContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
}
