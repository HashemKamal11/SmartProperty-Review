using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.Api.Infrastructure.Notifications;

/// <summary>
/// Stand-in <see cref="ISmsSender"/> that delivers nothing. It logs that a message was requested, and only in
/// Development also logs the message text so a developer can read the verification code. No SMS provider has been
/// chosen yet; replace this registration with the real sender when one is.
/// </summary>
internal sealed class LoggingSmsSender(
    ILogger<LoggingSmsSender> logger,
    IHostEnvironment environment) : ISmsSender
{
    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment())
        {
            logger.LogInformation("SMS to {PhoneNumber} (not delivered, no provider configured): {Message}", phoneNumber, message);
        }
        else
        {
            logger.LogWarning("An SMS was requested but no SMS provider is configured; nothing was sent.");
        }

        return Task.CompletedTask;
    }
}
