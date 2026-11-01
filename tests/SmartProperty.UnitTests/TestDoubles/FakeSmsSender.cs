using SmartProperty.Application.Abstractions.Notifications;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>Records every message instead of sending it.</summary>
internal sealed class FakeSmsSender : ISmsSender
{
    public List<(string PhoneNumber, string Message)> Sent { get; } = [];

    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        Sent.Add((phoneNumber, message));

        return Task.CompletedTask;
    }

    /// <summary>The six-digit code in the newest message sent.</summary>
    public string LatestCode()
    {
        return new string(Sent[^1].Message
            .SkipWhile(character => !char.IsAsciiDigit(character))
            .TakeWhile(char.IsAsciiDigit)
            .ToArray());
    }
}
