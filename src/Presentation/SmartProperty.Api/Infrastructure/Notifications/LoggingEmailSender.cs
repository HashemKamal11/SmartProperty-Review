using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.Api.Infrastructure.Notifications;

/// <summary>
/// Stand-in <see cref="IEmailSender"/> used while no SMTP host is configured. It delivers nothing: it logs that a
/// message was requested, and only in Development also logs the message text so a developer can read the code.
/// </summary>
internal sealed class LoggingEmailSender(
    ILogger<LoggingEmailSender> logger,
    IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment())
        {
            logger.LogInformation(
                "Email to {ToEmail} (not delivered, no SMTP host configured): {Subject}\n{Body}",
                toEmail,
                subject,
                body);
        }
        else
        {
            logger.LogWarning("An email was requested but no SMTP host is configured; nothing was sent.");
        }

        return Task.CompletedTask;
    }
}
