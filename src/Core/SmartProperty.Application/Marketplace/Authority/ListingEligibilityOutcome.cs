namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// The three possible answers to "may this user list this property for this purpose?".
/// </summary>
public enum ListingEligibilityOutcome
{
    Eligible = 1,
    NotEligible = 2,
    Unavailable = 3
}
