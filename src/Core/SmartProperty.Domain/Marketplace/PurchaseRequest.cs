namespace SmartProperty.Domain.Marketplace;

public sealed class PurchaseRequest
{
    private PurchaseRequest()
    {
    }

    public PurchaseRequest(Guid id, PropertyListing listing, Guid buyerUserId, DateTimeOffset submittedAt)
    {
        ValidateRequiredId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(listing);
        ValidateRequiredId(buyerUserId, nameof(buyerUserId));

        if (listing.Purpose != ListingPurpose.Sale)
        {
            throw new ArgumentException("A purchase request requires a Sale listing.", nameof(listing));
        }

        listing.EnsureCanAcceptRequests(submittedAt);

        Id = id;
        ListingId = listing.Id;
        PropertyId = listing.PropertyId;
        BuyerUserId = buyerUserId;
        Status = PurchaseRequestStatus.Submitted;
        SubmittedAt = submittedAt;
        UpdatedAt = submittedAt;
    }

    public Guid Id { get; private set; }
    public Guid ListingId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid BuyerUserId { get; private set; }
    public PurchaseRequestStatus Status { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public void Reject(DateTimeOffset rejectedAt)
    {
        EnsureSubmitted(rejectedAt);
        TransitionTo(PurchaseRequestStatus.Rejected, rejectedAt);
    }

    public void Withdraw(DateTimeOffset withdrawnAt)
    {
        EnsureSubmitted(withdrawnAt);
        TransitionTo(PurchaseRequestStatus.WithdrawnByRequester, withdrawnAt);
    }

    internal void EnsureCanAccept(DateTimeOffset acceptedAt)
    {
        EnsureSubmitted(acceptedAt);
    }

    internal void Accept(DateTimeOffset acceptedAt)
    {
        EnsureCanAccept(acceptedAt);
        TransitionTo(PurchaseRequestStatus.Accepted, acceptedAt);
    }

    internal void EnsureCanCloseAsUnavailable(DateTimeOffset closedAt)
    {
        if (Status == PurchaseRequestStatus.Submitted)
        {
            ValidateTransitionTime(closedAt, nameof(closedAt));
        }
    }

    internal void CloseAsUnavailable(DateTimeOffset closedAt)
    {
        EnsureCanCloseAsUnavailable(closedAt);

        if (Status == PurchaseRequestStatus.Submitted)
        {
            TransitionTo(PurchaseRequestStatus.ClosedListingUnavailable, closedAt);
        }
    }

    private void EnsureSubmitted(DateTimeOffset transitionAt)
    {
        if (Status != PurchaseRequestStatus.Submitted)
        {
            throw new InvalidOperationException("Only a submitted purchase request can change status.");
        }

        ValidateTransitionTime(transitionAt, nameof(transitionAt));
    }

    private void TransitionTo(PurchaseRequestStatus status, DateTimeOffset transitionAt)
    {
        Status = status;
        DecidedAt = transitionAt;
        UpdatedAt = transitionAt;
    }

    private void ValidateTransitionTime(DateTimeOffset value, string parameterName)
    {
        if (value < UpdatedAt)
        {
            throw new ArgumentException("Transition date cannot be earlier than the current update date.", parameterName);
        }
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
