using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only <see cref="IEmailSender"/> that keeps every message instead of sending it, so a test can read the
/// code a real user would have received. It exists only in test infrastructure.
/// </summary>
internal sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<(string ToEmail, string Subject, string Body)> _sent = new();

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue((toEmail, subject, body));

        return Task.CompletedTask;
    }

    public int CountFor(string toEmail)
    {
        return _sent.Count(email => email.ToEmail == toEmail);
    }

    /// <summary>The six-digit code in the newest message sent to <paramref name="toEmail"/>.</summary>
    public string LatestCodeFor(string toEmail)
    {
        var body = _sent.Last(email => email.ToEmail == toEmail).Body;

        return SixDigits().Match(body).Value;
    }

    [GeneratedRegex(@"\d{6}")]
    private static partial Regex SixDigits();
}
