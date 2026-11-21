namespace Security_Camera_Backend.Services.Notifications;

public sealed record OutboundEmail(
    string To,
    string Subject,
    string TextBody,
    string HtmlBody,
    string? AttachmentFileName = null,
    byte[]? AttachmentContent = null,
    string AttachmentContentType = "image/jpeg");

public interface ISmtpSender
{
    Task SendAsync(OutboundEmail email, CancellationToken ct);
}
