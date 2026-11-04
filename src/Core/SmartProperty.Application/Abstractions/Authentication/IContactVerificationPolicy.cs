namespace SmartProperty.Application.Abstractions.Authentication;

/// <summary>
/// Deployment-level switches for how a registered contact is proved.
/// </summary>
public interface IContactVerificationPolicy
{
    /// <summary>
    /// When true, registration marks the phone number as verified straight away and sends no SMS. This is a stop-gap
    /// for a deployment that has no working SMS provider yet: it proves nothing about the number, so it must be off
    /// once real SMS delivery exists. The email is still verified by code either way.
    /// </summary>
    bool AutoVerifyPhone { get; }
}
