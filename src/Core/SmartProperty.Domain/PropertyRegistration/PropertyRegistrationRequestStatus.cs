namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// Lifecycle of a property registration request. Approved, Rejected, and Withdrawn are terminal.
/// </summary>
public enum PropertyRegistrationRequestStatus
{
    Draft = 1,
    UnderReview = 2,
    ChangesRequired = 3,
    Approved = 4,
    Rejected = 5,
    Withdrawn = 6
}
