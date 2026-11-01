namespace SmartProperty.Domain.Marketplace;

public sealed class RentalRequest
{
    private RentalRequest()
    {
    }

    public RentalRequest(Guid id, PropertyListing listing, Guid renterUserId, DateTimeOffset submittedAt)
    {
        ValidateRequiredId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(listing);
        ValidateRequiredId(renterUserId, nameof(renterUserId));

        if (listing.Purpose != ListingPurpose.Rent)
        {
            throw new ArgumentException("A rental request requires a Rent listing.", nameof(listing));
        }

        listing.EnsureCanAcceptRequests(submittedAt);

        Id = id;
        ListingId = listing.Id;
        PropertyId = listing.PropertyId;
        RenterUserId = renterUserId;
        Status = RentalRequestStatus.Submitted;
        SubmittedAt = submittedAt;
        UpdatedAt = submittedAt;
    }

    public Guid Id { get; private set; }
    public Guid ListingId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid RenterUserId { get; private set; }
    public RentalRequestStatus Status { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public void Reject(DateTimeOffset rejectedAt)
    {
        EnsureSubmitted(rejectedAt);
        TransitionTo(RentalRequestStatus.Rejected, rejectedAt);
    }

    public void Withdraw(DateTimeOffset withdrawnAt)
    {
        EnsureSubmitted(withdrawnAt);
        TransitionTo(RentalRequestStatus.WithdrawnByRequester, withdrawnAt);
    }

    internal void EnsureCanAccept(DateTimeOffset acceptedAt)
    {
        EnsureSubmitted(acceptedAt);
    }

    internal void Accept(DateTimeOffset acceptedAt)
    {
        EnsureCanAccept(acceptedAt);
        TransitionTo(RentalRequestStatus.Accepted, acceptedAt);
    }

    internal void EnsureCanCloseAsUnavailable(DateTimeOffset closedAt)
    {
        if (Status == RentalRequestStatus.Submitted)
        {
            ValidateTransitionTime(closedAt, nameof(closedAt));
        }
    }

    internal void CloseAsUnavailable(DateTimeOffset closedAt)
    {
        EnsureCanCloseAsUnavailable(closedAt);

        if (Status == RentalRequestStatus.Submitted)
        {
            TransitionTo(RentalRequestStatus.ClosedListingUnavailable, closedAt);
        }
    }

    private void EnsureSubmitted(DateTimeOffset transitionAt)
    {
        if (Status != RentalRequestStatus.Submitted)
        {
            throw new InvalidOperationException("Only a submitted rental request can change status.");
        }

        ValidateTransitionTime(transitionAt, nameof(transitionAt));
    }

    private void TransitionTo(RentalRequestStatus status, DateTimeOffset transitionAt)
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
