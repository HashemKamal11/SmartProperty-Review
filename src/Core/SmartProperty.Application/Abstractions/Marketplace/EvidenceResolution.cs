using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// The evidence references a trusted provider resolved for a set of evidence ids, or the fact that it could not.
/// </summary>
/// <remarks>
/// The references pin each piece of evidence to an exact stored version and digest. An id the provider does not
/// recognize, or does not hold for this user, property and purpose, is simply absent, so the caller sees fewer
/// references than ids and fails closed. An unavailable provider is its own outcome and never an empty success.
/// </remarks>
public sealed class EvidenceResolution
{
    private EvidenceResolution(IReadOnlyList<ListingEvidenceReference>? references)
    {
        References = references;
    }

    public bool IsAvailable => References is not null;

    /// <summary>The resolved references; null exactly when the provider was unavailable.</summary>
    public IReadOnlyList<ListingEvidenceReference>? References { get; }

    public static EvidenceResolution Resolved(IReadOnlyList<ListingEvidenceReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (references.Any(reference => reference is null))
        {
            throw new ArgumentException("Evidence references must not contain null.", nameof(references));
        }

        return new EvidenceResolution(references);
    }

    public static EvidenceResolution Unavailable()
    {
        return new EvidenceResolution(references: null);
    }
}
