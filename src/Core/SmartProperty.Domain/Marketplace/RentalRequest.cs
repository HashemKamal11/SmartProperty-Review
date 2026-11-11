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

    public const int MaxMessageLength = 1000;
    public const int MaxStayDays = 3650;

    /// <summary>
    /// A dated rental request made against the approved version of a Rent listing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The price is copied from the pricing option the customer chose, never supplied by the customer. The pricing
    /// period says what the amount is charged per; the stay is the start and end dates, which are separate. The
    /// check-in and check-out times and the time zone are copied from the approved version, so the request keeps its
    /// meaning if the listing is edited later. The stay is the half-open range [start, end): the end date is the
    /// check-out day, so another stay may begin on it.
    /// </para>
    /// </remarks>
    public RentalRequest(
        Guid id,
        PropertyListing listing,
        Guid renterUserId,
        DateTimeOffset submittedAt,
        Guid submissionId,
        RentPricingOption pricing,
        DateOnly startDate,
        DateOnly endDate,
        TimeOnly checkInTime,
        TimeOnly checkOutTime,
        string timeZoneId,
        string? message)
        : this(id, listing, renterUserId, submittedAt)
    {
        ValidateRequiredId(submissionId, nameof(submissionId));
        ArgumentNullException.ThrowIfNull(pricing);
        RequestTerms.ValidateMessage(message, MaxMessageLength);
        RequestTerms.ValidateTimeZone(timeZoneId);

        if (listing.PublishedSubmissionId != submissionId)
        {
            throw new ArgumentException("The request must be made against the listing's current approved version.", nameof(submissionId));
        }

        if (endDate <= startDate)
        {
            throw new ArgumentException("The rental must end after it starts.", nameof(endDate));
        }

        if (endDate.DayNumber - startDate.DayNumber > MaxStayDays)
        {
            throw new ArgumentException("The rental is longer than the supported maximum.", nameof(endDate));
        }

        SubmissionId = submissionId;
        PricingPeriod = pricing.Period;
        Amount = pricing.Amount;
        Currency = pricing.Currency;
        StartDate = startDate;
        EndDate = endDate;
        CheckInTime = checkInTime;
        CheckOutTime = checkOutTime;
        TimeZoneId = timeZoneId;
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    /// <summary>The approved version this request was made against. Null only for a request that predates versions.</summary>
    public Guid? SubmissionId { get; private set; }

    public RentPeriod? PricingPeriod { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    /// <summary>First night of the stay, in the property's local calendar.</summary>
    public DateOnly? StartDate { get; private set; }

    /// <summary>Check-out day, exclusive: another stay may start on it.</summary>
    public DateOnly? EndDate { get; private set; }

    public TimeOnly? CheckInTime { get; private set; }

    public TimeOnly? CheckOutTime { get; private set; }

    public string? TimeZoneId { get; private set; }

    public string? Message { get; private set; }

    /// <summary>True for a request that carries dates. A request from before dated rentals has none.</summary>
    public bool IsDated => StartDate is not null && EndDate is not null;

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
