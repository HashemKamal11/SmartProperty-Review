namespace SmartProperty.Domain.Marketplace;

public enum CommitmentKind
{
    Sale = 1,
    Rental = 2
}

/// <summary>
/// Where an accepted request stands. Only the first three keep the property committed.
/// </summary>
public enum CommitmentStatus
{
    /// <summary>Accepted; the agreement has not been signed and the signing deadline has not passed.</summary>
    PendingSignature = 1,

    /// <summary>The agreement is signed. It is never cancelled by the platform.</summary>
    Signed = 2,

    /// <summary>A rental whose tenant has taken possession. The dates stay blocked until the return is confirmed.</summary>
    Occupied = 3,

    /// <summary>Finished: a rental was returned, or a sale was handed over. Releases the dates.</summary>
    Completed = 4,

    /// <summary>The agreement was not signed before the deadline. Releases the dates; the history stays.</summary>
    Expired = 5
}

/// <summary>
/// An accepted request: a dated rental or a sale that the property is now committed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces the permanent reservation.</b> A rental commits the property only for its own dates, [start, end),
/// so non-overlapping future rentals can be accepted, and a stay that ends on a day frees that day for the next.
/// Overlap is refused by the database itself, not only by the check made beforehand.
/// </para>
/// <para>
/// <b>No background job.</b> A commitment still waiting for a signature when its deadline passes is expired the next
/// time anything touches the property, inside that transaction (<see cref="IsOverdue"/> and <see cref="Expire"/>),
/// and readers ignore an overdue one, so nothing depends on a timer having run. A signed or occupied commitment is
/// never expired or cancelled automatically.
/// </para>
/// <para>
/// <b>Handover.</b> A rental is occupied from the confirmed handover to the tenant until the owner confirms the
/// return; only then is it complete. Nothing infers the end of an occupancy from the calendar, so an overstay
/// stays visible instead of silently freeing the property. Legal agreements, payments and ownership transfer are
/// outside this model: the confirmations are the owner's statements, nothing more.
/// </para>
/// </remarks>
public sealed class PropertyCommitment
{
    private PropertyCommitment()
    {
    }

    public Guid Id { get; private set; }
    public Guid PropertyId { get; private set; }
    public CommitmentKind Kind { get; private set; }
    public CommitmentStatus Status { get; private set; }
    public Guid ListingId { get; private set; }
    public Guid SubmissionId { get; private set; }
    public Guid? PurchaseRequestId { get; private set; }
    public Guid? RentalRequestId { get; private set; }

    /// <summary>The customer: the buyer or the tenant.</summary>
    public Guid CustomerUserId { get; private set; }

    /// <summary>The advertiser who accepted: the author of the approved version.</summary>
    public Guid OwnerUserId { get; private set; }

    public RentPeriod? PricingPeriod { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public TimeOnly? CheckInTime { get; private set; }
    public TimeOnly? CheckOutTime { get; private set; }
    public string? TimeZoneId { get; private set; }

    public DateTimeOffset AcceptedAt { get; private set; }
    public DateTimeOffset SigningDeadline { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public DateTimeOffset? HandedOverAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? ExpiredAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static bool KeepsPropertyCommitted(CommitmentStatus status) =>
        status is CommitmentStatus.PendingSignature or CommitmentStatus.Signed or CommitmentStatus.Occupied;

    /// <summary>Whether the commitment still blocks the property at this moment, taking an overdue signature into account.</summary>
    public bool IsActiveAt(DateTimeOffset at) =>
        KeepsPropertyCommitted(Status) && !IsOverdue(at);

    /// <summary>True when the signature is still missing and the deadline has passed.</summary>
    public bool IsOverdue(DateTimeOffset at) =>
        Status == CommitmentStatus.PendingSignature && at >= SigningDeadline;

    /// <summary>
    /// Whether this commitment has ended in a way that lets the listings it closed come back: the signature deadline
    /// passed, or a rental was returned. A completed sale never does, since the property has changed hands.
    /// </summary>
    public bool AllowsRelistAt(DateTimeOffset at) =>
        Status == CommitmentStatus.Expired
        || IsOverdue(at)
        || (Status == CommitmentStatus.Completed && Kind == CommitmentKind.Rental);

    /// <summary>Daily and weekly rentals are short stays: an occupancy that blocks selling the property.</summary>
    public bool IsShortStay => Kind == CommitmentKind.Rental && PricingPeriod is RentPeriod.Daily or RentPeriod.Weekly;

    /// <summary>
    /// Whether this commitment's dates overlap the half-open range [start, end). A sale has no dates.
    /// </summary>
    public bool Overlaps(DateOnly start, DateOnly end) =>
        Kind == CommitmentKind.Rental && StartDate is { } s && EndDate is { } e && s < end && start < e;

    /// <summary>The owner confirms the agreement was signed. Only while the deadline has not passed.</summary>
    public void ConfirmSigned(DateTimeOffset at)
    {
        if (Status != CommitmentStatus.PendingSignature || IsOverdue(at))
        {
            throw new InvalidOperationException("Only a commitment still within its signing deadline can be confirmed as signed.");
        }

        ValidateTime(at);
        Status = CommitmentStatus.Signed;
        SignedAt = at;
        UpdatedAt = at;
    }

    /// <summary>
    /// The owner confirms the handover. A rental becomes occupied; a sale is completed, since ownership transfer is
    /// outside this model.
    /// </summary>
    public void ConfirmHandover(DateTimeOffset at)
    {
        if (Status != CommitmentStatus.Signed)
        {
            throw new InvalidOperationException("Only a signed commitment can be handed over.");
        }

        ValidateTime(at);
        HandedOverAt = at;
        UpdatedAt = at;

        if (Kind == CommitmentKind.Sale)
        {
            Status = CommitmentStatus.Completed;
            CompletedAt = at;
        }
        else
        {
            Status = CommitmentStatus.Occupied;
        }
    }

    /// <summary>The owner confirms the tenant returned the property. Releases the dates.</summary>
    public void ConfirmReturn(DateTimeOffset at)
    {
        if (Kind != CommitmentKind.Rental || Status != CommitmentStatus.Occupied)
        {
            throw new InvalidOperationException("Only an occupied rental can be returned.");
        }

        ValidateTime(at);
        Status = CommitmentStatus.Completed;
        CompletedAt = at;
        UpdatedAt = at;
    }

    /// <summary>Marks an unsigned commitment past its deadline as expired. Does nothing otherwise.</summary>
    public void Expire(DateTimeOffset at)
    {
        if (!IsOverdue(at))
        {
            return;
        }

        Status = CommitmentStatus.Expired;
        ExpiredAt = at;
        UpdatedAt = at > UpdatedAt ? at : UpdatedAt;
    }

    internal static PropertyCommitment ForRental(
        PropertyListing listing,
        RentalRequest request,
        Guid ownerUserId,
        DateTimeOffset acceptedAt,
        DateTimeOffset signingDeadline)
    {
        if (!request.IsDated || request.SubmissionId is not { } submissionId)
        {
            throw new InvalidOperationException("Only a dated rental request can be committed to.");
        }

        return new PropertyCommitment
        {
            Id = Guid.NewGuid(),
            PropertyId = listing.PropertyId,
            Kind = CommitmentKind.Rental,
            Status = CommitmentStatus.PendingSignature,
            ListingId = listing.Id,
            SubmissionId = submissionId,
            RentalRequestId = request.Id,
            CustomerUserId = request.RenterUserId,
            OwnerUserId = ownerUserId,
            PricingPeriod = request.PricingPeriod,
            Amount = request.Amount!.Value,
            Currency = request.Currency!,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            CheckInTime = request.CheckInTime,
            CheckOutTime = request.CheckOutTime,
            TimeZoneId = request.TimeZoneId,
            AcceptedAt = acceptedAt,
            SigningDeadline = signingDeadline,
            UpdatedAt = acceptedAt
        };
    }

    internal static PropertyCommitment ForSale(
        PropertyListing listing,
        PurchaseRequest request,
        Guid ownerUserId,
        DateTimeOffset acceptedAt,
        DateTimeOffset signingDeadline)
    {
        if (request.SubmissionId is not { } submissionId || request.OfferedAmount is not { } amount || request.Currency is null)
        {
            throw new InvalidOperationException("Only a purchase request with an offer can be committed to.");
        }

        return new PropertyCommitment
        {
            Id = Guid.NewGuid(),
            PropertyId = listing.PropertyId,
            Kind = CommitmentKind.Sale,
            Status = CommitmentStatus.PendingSignature,
            ListingId = listing.Id,
            SubmissionId = submissionId,
            PurchaseRequestId = request.Id,
            CustomerUserId = request.BuyerUserId,
            OwnerUserId = ownerUserId,
            Amount = amount,
            Currency = request.Currency,
            AcceptedAt = acceptedAt,
            SigningDeadline = signingDeadline,
            UpdatedAt = acceptedAt
        };
    }

    private void ValidateTime(DateTimeOffset at)
    {
        if (at < UpdatedAt)
        {
            throw new ArgumentException("A transition cannot precede the last update.", nameof(at));
        }
    }
}
