using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.Api.Infrastructure.Notifications;

/// <summary>
/// Sends plain-text email through the SMTP server named in <see cref="SmtpOptions"/>. A failed send throws, so the
/// caller never mistakes it for a delivered message.
/// </summary>
internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };

        message.To.Add(toEmail);

        // SmtpClient is not thread-safe, so each message gets its own.
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)TimeSpan.FromSeconds(settings.TimeoutSeconds).TotalMilliseconds
        };

        if (!string.IsNullOrEmpty(settings.UserName))
        {
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
