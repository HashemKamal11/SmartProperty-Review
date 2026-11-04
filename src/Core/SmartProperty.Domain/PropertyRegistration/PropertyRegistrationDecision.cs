namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// Outcome a platform reviewer records against a submitted version of a registration request.
/// </summary>
public enum PropertyRegistrationDecision
{
    Approved = 1,
    ChangesRequested = 2,
    Rejected = 3
}
