namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// One purpose-specific marketplace identity for a canonical property. A property may have one Sale identity and
/// one Rent identity; their publication is independent until a request establishes property-wide exclusivity.
/// </summary>
public sealed class PropertyListing
{
    private PropertyListing()
    {
    }

    public PropertyListing(Guid id, Guid propertyId, ListingPurpose purpose, DateTimeOffset createdAt)
    {
        ValidateRequiredId(id, nameof(id));
        ValidateRequiredId(propertyId, nameof(propertyId));

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Listing purpose is not supported.");
        }

        Id = id;
        PropertyId = propertyId;
        Purpose = purpose;
        PublicationStatus = ListingPublicationStatus.Unpublished;
        AvailabilityStatus = ListingAvailabilityStatus.Open;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid PropertyId { get; private set; }
    public ListingPurpose Purpose { get; private set; }
    public ListingPublicationStatus PublicationStatus { get; private set; }
    public ListingAvailabilityStatus AvailabilityStatus { get; private set; }
    public Guid? PublishedSubmissionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? WithdrawnAt { get; private set; }

    /// <summary>Monotonic local marker; authoritative property-wide availability must also be checked.</summary>
    public bool ClosedByReservation { get; private set; }

    /// <summary>
    /// The owner's choice: when the accepted request that closed this listing ends without a lasting deal (the
    /// signature deadline passed, or a rental was returned), a copy of the last approved version is submitted for
    /// review automatically. Off by default. It never publishes anything by itself.
    /// </summary>
    public bool RelistAutomatically { get; private set; }

    /// <summary>
    /// The commitment whose acceptance closed this listing, or null when it is open, was withdrawn by the owner, or was
    /// never published. Cleared when the listing is published or withdrawn again.
    /// </summary>
    public Guid? ClosedByCommitmentId { get; private set; }

    /// <summary>Records the owner's automatic relisting choice. A preference only: no state or history changes.</summary>
    public void SetRelistAutomatically(bool enabled)
    {
        RelistAutomatically = enabled;
    }

    /// <summary>
    /// Whether a copy of the last approved version may bring the listing back: it was published, then withdrawn or
    /// closed by an acceptance, and no reservation holds it.
    /// </summary>
    public bool CanBeRelisted =>
        PublicationStatus == ListingPublicationStatus.Withdrawn
        && AvailabilityStatus == ListingAvailabilityStatus.Closed
        && !ClosedByReservation
        && PublishedSubmissionId.HasValue;

    internal void EnsureCanPublishApproved(ListingSubmission submission, DateTimeOffset at)
    {
        if (ClosedByReservation || AvailabilityStatus == ListingAvailabilityStatus.Reserved
            || (PublicationStatus == ListingPublicationStatus.Unpublished && AvailabilityStatus != ListingAvailabilityStatus.Open))
            throw new InvalidOperationException("Reservation closure prevents publication.");
        if (PublicationStatus == ListingPublicationStatus.Withdrawn
            && (WithdrawnAt is null || submission.SubmittedAt is null || submission.SubmittedAt < WithdrawnAt
                || submission.Id == PublishedSubmissionId))
            throw new InvalidOperationException("Withdrawal requires a freshly submitted and approved version.");
        ValidateTransitionTime(at, nameof(at));
    }

    // Called only after ListingSubmission has validated the entire review/publication graph.
    internal void ApplyApprovedPublication(ListingSubmission submission, DateTimeOffset at)
    {
        PublicationStatus = ListingPublicationStatus.Published;
        AvailabilityStatus = ListingAvailabilityStatus.Open;
        PublishedSubmissionId = submission.Id;
        PublishedAt = at;
        UpdatedAt = at;
        ClosedByCommitmentId = null;
    }

    /// <summary>Purpose-local withdrawal; a fresh independent approval is required for republication.</summary>
    public void Withdraw(DateTimeOffset withdrawnAt)
    {
        if (!CanAcceptRequests)
        {
            throw new InvalidOperationException("Only a published, open listing can be withdrawn.");
        }

        ValidateTransitionTime(withdrawnAt, nameof(withdrawnAt));

        PublicationStatus = ListingPublicationStatus.Withdrawn;
        AvailabilityStatus = ListingAvailabilityStatus.Closed;
        WithdrawnAt = withdrawnAt;
        UpdatedAt = withdrawnAt;
        ClosedByCommitmentId = null;
    }

    /// <summary>
    /// Takes a published advertisement off the marketplace because a request for the property was accepted. It is
    /// the same state as a withdrawal, so the owner advertises again only by an explicit new submission that is
    /// reviewed afresh. A listing that is not published is left exactly as it is, and a closure by a legacy
    /// reservation is never touched. The commitment is remembered, so the listing can be relisted when it ends.
    /// </summary>
    internal void CloseForAcceptance(DateTimeOffset closedAt, Guid commitmentId)
    {
        if (PublicationStatus != ListingPublicationStatus.Published)
        {
            return;
        }

        ValidateRequiredId(commitmentId, nameof(commitmentId));
        ValidateTransitionTime(closedAt, nameof(closedAt));

        PublicationStatus = ListingPublicationStatus.Withdrawn;
        AvailabilityStatus = ListingAvailabilityStatus.Closed;
        WithdrawnAt = closedAt;
        UpdatedAt = closedAt;
        ClosedByCommitmentId = commitmentId;
    }

    internal bool CanAcceptRequests =>
        PublicationStatus == ListingPublicationStatus.Published
        && AvailabilityStatus == ListingAvailabilityStatus.Open;

    internal void EnsureCanAcceptRequests(DateTimeOffset requestedAt)
    {
        if (!CanAcceptRequests)
        {
            throw new InvalidOperationException("The listing is not published and open.");
        }

        ValidateTransitionTime(requestedAt, nameof(requestedAt));
    }

    internal void EnsureCanReserve(DateTimeOffset reservedAt)
    {
        if (!CanAcceptRequests)
        {
            throw new InvalidOperationException("Only a published, open listing can be reserved.");
        }

        ValidateTransitionTime(reservedAt, nameof(reservedAt));
    }

    internal void Reserve(DateTimeOffset reservedAt)
    {
        EnsureCanReserve(reservedAt);

        PublicationStatus = ListingPublicationStatus.Withdrawn;
        AvailabilityStatus = ListingAvailabilityStatus.Reserved;
        ClosedByReservation = true;
        WithdrawnAt = reservedAt;
        UpdatedAt = reservedAt;
    }

    internal void EnsureCanCloseForPropertyReservation(DateTimeOffset closedAt)
    {
        if (AvailabilityStatus == ListingAvailabilityStatus.Reserved)
        {
            throw new InvalidOperationException("A reserved listing cannot be closed by another reservation.");
        }

        if (!ClosedByReservation)
        {
            ValidateTransitionTime(closedAt, nameof(closedAt));
        }
    }

    internal void CloseForPropertyReservation(DateTimeOffset closedAt)
    {
        EnsureCanCloseForPropertyReservation(closedAt);

        ClosedByReservation = true;
        if (AvailabilityStatus == ListingAvailabilityStatus.Closed)
        {
            return;
        }

        if (PublicationStatus == ListingPublicationStatus.Published)
        {
            PublicationStatus = ListingPublicationStatus.Withdrawn;
            WithdrawnAt = closedAt;
        }

        AvailabilityStatus = ListingAvailabilityStatus.Closed;
        UpdatedAt = closedAt;
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
