namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Accepting a request: the winning request becomes a commitment, the property's current advertisements close, and
/// every other pending request is closed with its history kept.
/// </summary>
/// <remarks>
/// <para>
/// These are in-memory transitions over a snapshot the caller loaded completely under the property lock. The caller
/// must have excluded the accepted request from the competitor sets, expired overdue commitments, and checked the
/// rules that depend on other commitments (overlap, the sale restrictions); the database enforces overlap and the
/// single active sale again when the unit of work is saved.
/// </para>
/// <para>
/// <b>What is never touched.</b> Existing commitments are not part of the inputs, so an approved booking or a signed
/// agreement cannot be cancelled by an acceptance. Only advertisements that are currently published are closed; a
/// draft or a version awaiting review is left alone and is judged on its own merits when it is reviewed.
/// </para>
/// </remarks>
public static class CommitmentAcceptance
{
    public static PropertyCommitment AcceptRental(
        PropertyListing rentListing,
        PropertyListing? saleListing,
        RentalRequest winner,
        IReadOnlyCollection<RentalRequest> otherRentalRequests,
        IReadOnlyCollection<PurchaseRequest> otherPurchaseRequests,
        Guid ownerUserId,
        DateTimeOffset at,
        DateTimeOffset signingDeadline)
    {
        ArgumentNullException.ThrowIfNull(rentListing);
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(otherRentalRequests);
        ArgumentNullException.ThrowIfNull(otherPurchaseRequests);

        if (rentListing.Purpose != ListingPurpose.Rent || winner.ListingId != rentListing.Id || winner.PropertyId != rentListing.PropertyId)
        {
            throw new ArgumentException("The request does not belong to the Rent listing.", nameof(winner));
        }

        EnsureSameProperty(rentListing, saleListing, ListingPurpose.Sale);
        EnsureNotWinner(winner.Id, otherRentalRequests.Select(request => request.Id));
        ValidateDeadline(at, signingDeadline);

        // Everything is validated before anything changes.
        winner.EnsureCanAccept(at);
        var commitment = PropertyCommitment.ForRental(rentListing, winner, ownerUserId, at, signingDeadline);

        winner.Accept(at);
        rentListing.CloseForAcceptance(at, commitment.Id);
        saleListing?.CloseForAcceptance(at, commitment.Id);

        foreach (var request in otherRentalRequests)
        {
            request.CloseAsUnavailable(at);
        }

        foreach (var request in otherPurchaseRequests)
        {
            request.CloseAsUnavailable(at);
        }

        return commitment;
    }

    public static PropertyCommitment AcceptSale(
        PropertyListing saleListing,
        PropertyListing? rentListing,
        PurchaseRequest winner,
        IReadOnlyCollection<PurchaseRequest> otherPurchaseRequests,
        IReadOnlyCollection<RentalRequest> otherRentalRequests,
        Guid ownerUserId,
        DateTimeOffset at,
        DateTimeOffset signingDeadline)
    {
        ArgumentNullException.ThrowIfNull(saleListing);
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(otherPurchaseRequests);
        ArgumentNullException.ThrowIfNull(otherRentalRequests);

        if (saleListing.Purpose != ListingPurpose.Sale || winner.ListingId != saleListing.Id || winner.PropertyId != saleListing.PropertyId)
        {
            throw new ArgumentException("The request does not belong to the Sale listing.", nameof(winner));
        }

        EnsureSameProperty(saleListing, rentListing, ListingPurpose.Rent);
        EnsureNotWinner(winner.Id, otherPurchaseRequests.Select(request => request.Id));
        ValidateDeadline(at, signingDeadline);

        winner.EnsureCanAccept(at);
        var commitment = PropertyCommitment.ForSale(saleListing, winner, ownerUserId, at, signingDeadline);

        winner.Accept(at);
        saleListing.CloseForAcceptance(at, commitment.Id);
        rentListing?.CloseForAcceptance(at, commitment.Id);

        foreach (var request in otherPurchaseRequests)
        {
            request.CloseAsUnavailable(at);
        }

        foreach (var request in otherRentalRequests)
        {
            request.CloseAsUnavailable(at);
        }

        return commitment;
    }

    private static void EnsureSameProperty(PropertyListing winning, PropertyListing? other, ListingPurpose expected)
    {
        if (other is not null && (other.PropertyId != winning.PropertyId || other.Purpose != expected))
        {
            throw new ArgumentException("The other listing must be the opposite purpose of the same property.", nameof(other));
        }
    }

    private static void EnsureNotWinner(Guid winnerId, IEnumerable<Guid> competitorIds)
    {
        if (competitorIds.Contains(winnerId))
        {
            throw new ArgumentException("The accepted request must not be among its competitors.");
        }
    }

    private static void ValidateDeadline(DateTimeOffset at, DateTimeOffset deadline)
    {
        if (deadline <= at)
        {
            throw new ArgumentException("The signing deadline must be after the acceptance.", nameof(deadline));
        }
    }
}
