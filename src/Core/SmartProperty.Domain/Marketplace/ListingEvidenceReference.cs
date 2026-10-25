namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Immutable reference to private evidence bytes. Opaque IDs must be resolved by a protected storage
/// boundary; this object contains no storage address, download URL or access credential.
/// A separate reference is required when the bytes, version, property or purpose changes.
/// </summary>
public sealed class ListingEvidenceReference
{
    public ListingEvidenceReference(Guid evidenceId, Guid versionId, Guid propertyId, ListingPurpose purpose, string sha256)
    {
        if (evidenceId == Guid.Empty || versionId == Guid.Empty || propertyId == Guid.Empty)
            throw new ArgumentException("Evidence, version and property identities must be nonempty.");
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("The digest must be a 64-character SHA-256 hexadecimal value.", nameof(sha256));

        EvidenceId = evidenceId;
        VersionId = versionId;
        PropertyId = propertyId;
        Purpose = purpose;
        Sha256 = sha256.ToUpperInvariant();
    }

    public Guid EvidenceId { get; }
    public Guid VersionId { get; }
    public Guid PropertyId { get; }
    public ListingPurpose Purpose { get; }
    public string Sha256 { get; }
}
