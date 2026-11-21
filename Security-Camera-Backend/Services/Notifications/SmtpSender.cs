using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Security_Camera_Backend.Configuration;

namespace Security_Camera_Backend.Services.Notifications;

/// <summary>Thin MailKit wrapper that opens one SMTP connection per message.</summary>
public sealed class SmtpSender(
    IOptions<SmtpOptions> smtpOptions,
    ILogger<SmtpSender> logger) : ISmtpSender
{
    public async Task SendAsync(OutboundEmail email, CancellationToken ct)
    {
        var options = smtpOptions.Value;

        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Smtp:Host is not configured; refusing to send an alert e-mail.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;

        var body = new BodyBuilder
        {
            TextBody = email.TextBody,
            HtmlBody = email.HtmlBody
        };

        if (email.AttachmentContent is not null && !string.IsNullOrWhiteSpace(email.AttachmentFileName))
        {
            body.Attachments.Add(
                email.AttachmentFileName,
                email.AttachmentContent,
                ContentType.Parse(email.AttachmentContentType));
        }

        message.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        var secureSocketOptions = options.UseStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.None;

        await client.ConnectAsync(options.Host, options.Port, secureSocketOptions, ct);

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            await client.AuthenticateAsync(options.Username, options.Password ?? string.Empty, ct);
        }

        await client.SendAsync(new SendRequest(message), ct);
        await client.DisconnectAsync(quit: true, ct);

        logger.LogDebug("Sent alert e-mail to {Recipient} with subject {Subject}.", email.To, email.Subject);
    }
}
