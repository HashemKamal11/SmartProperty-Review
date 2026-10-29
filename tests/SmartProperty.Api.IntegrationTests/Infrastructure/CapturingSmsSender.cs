using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A test-only <see cref="ISmsSender"/> that keeps every message instead of sending it, so a test can read the
/// verification code a real user would have received. It exists only in test infrastructure.
/// </summary>
internal sealed partial class CapturingSmsSender : ISmsSender
{
    private readonly ConcurrentQueue<(string PhoneNumber, string Message)> _sent = new();

    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue((phoneNumber, message));

        return Task.CompletedTask;
    }

    public int CountFor(string phoneNumber)
    {
        return _sent.Count(sms => sms.PhoneNumber == phoneNumber);
    }

    /// <summary>The six-digit code in the newest message sent to <paramref name="phoneNumber"/>.</summary>
    public string LatestCodeFor(string phoneNumber)
    {
        var message = _sent.Last(sms => sms.PhoneNumber == phoneNumber).Message;

        return SixDigits().Match(message).Value;
    }

    [GeneratedRegex(@"\d{6}")]
    private static partial Regex SixDigits();
}
