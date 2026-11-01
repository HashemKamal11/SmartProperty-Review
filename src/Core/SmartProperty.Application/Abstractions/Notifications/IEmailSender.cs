namespace SmartProperty.Application.Abstractions.Notifications;

/// <summary>
/// Sends a plain-text email. Implementations throw when the message cannot be handed to the mail server, so a
/// caller never mistakes a failed send for a delivered one.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}
