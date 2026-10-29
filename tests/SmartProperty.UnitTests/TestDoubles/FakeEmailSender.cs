using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>Records every message instead of sending it.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    public List<(string ToEmail, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        Sent.Add((toEmail, subject, body));

        return Task.CompletedTask;
    }

    /// <summary>The six-digit code in the newest message sent.</summary>
    public string LatestCode()
    {
        return new string(Sent[^1].Body
            .SkipWhile(character => !char.IsAsciiDigit(character))
            .TakeWhile(char.IsAsciiDigit)
            .ToArray());
    }
}
