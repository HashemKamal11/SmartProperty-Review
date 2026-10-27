namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// Why eligibility was refused. For internal decisions and audit only: the caller is only ever told that they are not
/// eligible, never which condition failed, so the grant model cannot be mapped one request at a time.
/// </summary>
public enum ListingEligibilityDenial
{
    InvalidRequest = 1,
    NoGrant = 2,
    MalformedGrant = 3,
    NotGrantee = 4,
    WrongProperty = 5,
    PurposeNotAllowed = 6,
    Revoked = 7,
    NotYetValid = 8,
    Expired = 9,
    ProvenanceMissing = 10,
    ProvenanceInconsistent = 11,
    IssuerIsGrantee = 12,
    ReviewerNotIndependent = 13,
    IssuerNotAuthorized = 14,
    UnsupportedRepresentation = 15,
    OwnerNotLinked = 16,
    GrantChanged = 17
}
