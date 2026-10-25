namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Records an accepted marketplace path for a canonical property and coordinates transitions over one complete,
/// caller-supplied in-memory snapshot. This type cannot prove that the snapshot is complete or that another
/// reservation does not exist. The Application/Persistence layer must load both listing identities and every
/// submitted Purchase and Rental request under one property-level lock and transaction, then persist all changes
/// atomically. Persistence must also enforce at most one reservation per PropertyId.
/// </summary>
public sealed class PropertyMarketplaceReservation
{
    private PropertyMarketplaceReservation()
    {
    }

    private PropertyMarketplaceReservation(
        Guid propertyId,
        ListingPurpose reservedPurpose,
        Guid winningListingId,
        Guid winningRequestId,
        DateTimeOffset reservedAt)
    {
        PropertyId = propertyId;
        ReservedPurpose = reservedPurpose;
        WinningListingId = winningListingId;
        WinningRequestId = winningRequestId;
        ReservedAt = reservedAt;
    }

    public Guid PropertyId { get; private set; }
    public ListingPurpose ReservedPurpose { get; private set; }
    public Guid WinningListingId { get; private set; }
    public Guid WinningRequestId { get; private set; }
    public DateTimeOffset ReservedAt { get; private set; }

    /// <summary>
    /// Applies the approved Purchase acceptance transitions to the complete snapshot supplied by the caller.
    /// Snapshot completeness, property-level serialization, reservation uniqueness and atomic persistence are
    /// mandatory Application/Persistence responsibilities.
    /// </summary>
    public static PropertyMarketplaceReservation AcceptPurchase(
        PropertyListing saleListing,
        PropertyListing? rentListing,
        PurchaseRequest winningRequest,
        IReadOnlyCollection<PurchaseRequest> competingPurchaseRequests,
        IReadOnlyCollection<RentalRequest> competingRentalRequests,
        DateTimeOffset reservedAt)
    {
        ArgumentNullException.ThrowIfNull(saleListing);
        ArgumentNullException.ThrowIfNull(winningRequest);
        ArgumentNullException.ThrowIfNull(competingPurchaseRequests);
        ArgumentNullException.ThrowIfNull(competingRentalRequests);

        var purchaseSnapshot = CreateStableSnapshot(
            competingPurchaseRequests,
            nameof(competingPurchaseRequests),
            request => request.Id);
        var rentalSnapshot = CreateStableSnapshot(
            competingRentalRequests,
            nameof(competingRentalRequests),
            request => request.Id);

        EnsureListingPurpose(saleListing, ListingPurpose.Sale, nameof(saleListing));
        EnsureWinningPurchaseMatches(saleListing, winningRequest);
        EnsureOppositeListingMatches(saleListing, rentListing, ListingPurpose.Rent, nameof(rentListing));
        EnsurePurchaseCompetitorsMatch(saleListing, winningRequest, purchaseSnapshot);
        EnsureRentalCompetitorsMatch(saleListing.PropertyId, rentListing, rentalSnapshot);

        saleListing.EnsureCanReserve(reservedAt);
        rentListing?.EnsureCanCloseForPropertyReservation(reservedAt);
        winningRequest.EnsureCanAccept(reservedAt);
        EnsurePurchaseRequestsCanClose(purchaseSnapshot, reservedAt);
        EnsureRentalRequestsCanClose(rentalSnapshot, reservedAt);

        var reservation = new PropertyMarketplaceReservation(
            saleListing.PropertyId,
            ListingPurpose.Sale,
            saleListing.Id,
            winningRequest.Id,
            reservedAt);

        winningRequest.Accept(reservedAt);
        saleListing.Reserve(reservedAt);
        rentListing?.CloseForPropertyReservation(reservedAt);
        ClosePurchaseRequests(purchaseSnapshot, reservedAt);
        CloseRentalRequests(rentalSnapshot, reservedAt);

        return reservation;
    }

    /// <summary>
    /// Applies the approved Rental acceptance transitions to the complete snapshot supplied by the caller.
    /// Snapshot completeness, property-level serialization, reservation uniqueness and atomic persistence are
    /// mandatory Application/Persistence responsibilities.
    /// </summary>
    public static PropertyMarketplaceReservation AcceptRental(
        PropertyListing rentListing,
        PropertyListing? saleListing,
        RentalRequest winningRequest,
        IReadOnlyCollection<RentalRequest> competingRentalRequests,
        IReadOnlyCollection<PurchaseRequest> competingPurchaseRequests,
        DateTimeOffset reservedAt)
    {
        ArgumentNullException.ThrowIfNull(rentListing);
        ArgumentNullException.ThrowIfNull(winningRequest);
        ArgumentNullException.ThrowIfNull(competingRentalRequests);
        ArgumentNullException.ThrowIfNull(competingPurchaseRequests);

        var rentalSnapshot = CreateStableSnapshot(
            competingRentalRequests,
            nameof(competingRentalRequests),
            request => request.Id);
        var purchaseSnapshot = CreateStableSnapshot(
            competingPurchaseRequests,
            nameof(competingPurchaseRequests),
            request => request.Id);

        EnsureListingPurpose(rentListing, ListingPurpose.Rent, nameof(rentListing));
        EnsureWinningRentalMatches(rentListing, winningRequest);
        EnsureOppositeListingMatches(rentListing, saleListing, ListingPurpose.Sale, nameof(saleListing));
        EnsureRentalCompetitorsMatch(rentListing.PropertyId, rentListing, rentalSnapshot, winningRequest);
        EnsurePurchaseCompetitorsMatch(rentListing.PropertyId, saleListing, purchaseSnapshot);

        rentListing.EnsureCanReserve(reservedAt);
        saleListing?.EnsureCanCloseForPropertyReservation(reservedAt);
        winningRequest.EnsureCanAccept(reservedAt);
        EnsureRentalRequestsCanClose(rentalSnapshot, reservedAt);
        EnsurePurchaseRequestsCanClose(purchaseSnapshot, reservedAt);

        var reservation = new PropertyMarketplaceReservation(
            rentListing.PropertyId,
            ListingPurpose.Rent,
            rentListing.Id,
            winningRequest.Id,
            reservedAt);

        winningRequest.Accept(reservedAt);
        rentListing.Reserve(reservedAt);
        saleListing?.CloseForPropertyReservation(reservedAt);
        CloseRentalRequests(rentalSnapshot, reservedAt);
        ClosePurchaseRequests(purchaseSnapshot, reservedAt);

        return reservation;
    }

    private static TRequest[] CreateStableSnapshot<TRequest>(
        IReadOnlyCollection<TRequest> requests,
        string parameterName,
        Func<TRequest, Guid> idSelector)
        where TRequest : class
    {
        var snapshot = requests.ToArray();
        var identities = new HashSet<Guid>();

        foreach (var request in snapshot)
        {
            if (request is null)
            {
                throw new ArgumentException("A competing request collection cannot contain null entries.", parameterName);
            }

            if (!identities.Add(idSelector(request)))
            {
                throw new ArgumentException("A competing request collection cannot contain duplicate identities.", parameterName);
            }
        }

        return snapshot;
    }

    private static void EnsureListingPurpose(PropertyListing listing, ListingPurpose purpose, string parameterName)
    {
        if (listing.Purpose != purpose)
        {
            throw new ArgumentException($"The listing must have purpose {purpose}.", parameterName);
        }
    }

    private static void EnsureWinningPurchaseMatches(PropertyListing listing, PurchaseRequest request)
    {
        if (request.ListingId != listing.Id || request.PropertyId != listing.PropertyId)
        {
            throw new ArgumentException("The winning purchase request does not belong to the Sale listing.", nameof(request));
        }
    }

    private static void EnsureWinningRentalMatches(PropertyListing listing, RentalRequest request)
    {
        if (request.ListingId != listing.Id || request.PropertyId != listing.PropertyId)
        {
            throw new ArgumentException("The winning rental request does not belong to the Rent listing.", nameof(request));
        }
    }

    private static void EnsureOppositeListingMatches(
        PropertyListing winningListing,
        PropertyListing? oppositeListing,
        ListingPurpose expectedPurpose,
        string parameterName)
    {
        if (oppositeListing is null)
        {
            return;
        }

        if (oppositeListing.PropertyId != winningListing.PropertyId || oppositeListing.Purpose != expectedPurpose)
        {
            throw new ArgumentException("The opposite-purpose listing must belong to the same property.", parameterName);
        }
    }

    private static void EnsurePurchaseCompetitorsMatch(
        PropertyListing listing,
        PurchaseRequest winningRequest,
        IEnumerable<PurchaseRequest> requests)
    {
        foreach (var request in requests)
        {
            if (request.Id == winningRequest.Id)
            {
                throw new ArgumentException("The winning request must not be included among its competitors.", nameof(requests));
            }

            if (request.ListingId != listing.Id || request.PropertyId != listing.PropertyId)
            {
                throw new ArgumentException("Every competing purchase request must belong to the Sale listing.", nameof(requests));
            }
        }
    }

    private static void EnsurePurchaseCompetitorsMatch(
        Guid propertyId,
        PropertyListing? saleListing,
        IEnumerable<PurchaseRequest> requests)
    {
        foreach (var request in requests)
        {
            if (saleListing is null
                || request.ListingId != saleListing.Id
                || request.PropertyId != propertyId)
            {
                throw new ArgumentException("Every competing purchase request must belong to this property's Sale listing.", nameof(requests));
            }
        }
    }

    private static void EnsureRentalCompetitorsMatch(
        Guid propertyId,
        PropertyListing? rentListing,
        IEnumerable<RentalRequest> requests,
        RentalRequest? winningRequest = null)
    {
        foreach (var request in requests)
        {
            if (winningRequest is not null && request.Id == winningRequest.Id)
            {
                throw new ArgumentException("The winning request must not be included among its competitors.", nameof(requests));
            }

            if (rentListing is null
                || request.ListingId != rentListing.Id
                || request.PropertyId != propertyId)
            {
                throw new ArgumentException("Every competing rental request must belong to this property's Rent listing.", nameof(requests));
            }
        }
    }

    private static void EnsurePurchaseRequestsCanClose(
        IEnumerable<PurchaseRequest> requests,
        DateTimeOffset closedAt)
    {
        foreach (var request in requests)
        {
            request.EnsureCanCloseAsUnavailable(closedAt);
        }
    }

    private static void EnsureRentalRequestsCanClose(
        IEnumerable<RentalRequest> requests,
        DateTimeOffset closedAt)
    {
        foreach (var request in requests)
        {
            request.EnsureCanCloseAsUnavailable(closedAt);
        }
    }

    private static void ClosePurchaseRequests(IEnumerable<PurchaseRequest> requests, DateTimeOffset closedAt)
    {
        foreach (var request in requests)
        {
            request.CloseAsUnavailable(closedAt);
        }
    }

    private static void CloseRentalRequests(IEnumerable<RentalRequest> requests, DateTimeOffset closedAt)
    {
        foreach (var request in requests)
        {
            request.CloseAsUnavailable(closedAt);
        }
    }
}
