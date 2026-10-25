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

    /// <summary>
    /// Publishes the caller-validated submission reference. A nonempty identifier does not prove approval;
    /// the Application/Persistence layer must verify that the immutable submission belongs to this listing and
    /// purpose and has an approved independent review before invoking this method in the same transaction.
    /// </summary>
    public void Publish(Guid submissionId, DateTimeOffset publishedAt)
    {
        ValidateRequiredId(submissionId, nameof(submissionId));

        if (PublicationStatus != ListingPublicationStatus.Unpublished
            || AvailabilityStatus != ListingAvailabilityStatus.Open)
        {
            throw new InvalidOperationException("Only an unpublished, open listing can be published.");
        }

        ValidateTransitionTime(publishedAt, nameof(publishedAt));

        PublicationStatus = ListingPublicationStatus.Published;
        PublishedSubmissionId = submissionId;
        PublishedAt = publishedAt;
        UpdatedAt = publishedAt;
    }

    /// <summary>
    /// Withdraws this purpose only. It creates no property-wide reservation and has no effect on another listing.
    /// A withdrawn listing cannot be published again through <see cref="Publish"/>.
    /// </summary>
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
        WithdrawnAt = reservedAt;
        UpdatedAt = reservedAt;
    }

    internal void EnsureCanCloseForPropertyReservation(DateTimeOffset closedAt)
    {
        if (AvailabilityStatus == ListingAvailabilityStatus.Reserved)
        {
            throw new InvalidOperationException("A reserved listing cannot be closed by another reservation.");
        }

        if (AvailabilityStatus == ListingAvailabilityStatus.Open)
        {
            ValidateTransitionTime(closedAt, nameof(closedAt));
        }
    }

    internal void CloseForPropertyReservation(DateTimeOffset closedAt)
    {
        EnsureCanCloseForPropertyReservation(closedAt);

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
