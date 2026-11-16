namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;

/// <summary>
/// The owner's choice for a listing an accepted request closed, once that request ends without a deal (the signature
/// deadline passed, or the rental was returned). An owner preference, never a verdict about anything.
/// </summary>
public enum RelistMode
{
    /// <summary>The listing stays closed until the owner relists it. The default.</summary>
    Manual = 1,

    /// <summary>A copy of the last approved version is submitted for review in the nightly pass.</summary>
    Automatic = 2
}
