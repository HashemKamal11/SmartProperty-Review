namespace SmartProperty.Application.Abstractions.Notifications;

/// <summary>
/// Sends a text message to an E.164 phone number. Implementations throw when the message cannot be handed to the
/// provider, so a caller never mistakes a failed send for a delivered one.
/// </summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}
