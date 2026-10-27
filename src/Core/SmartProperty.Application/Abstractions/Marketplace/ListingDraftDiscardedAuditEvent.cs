using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// The audit fact that a never-submitted draft was discarded.
/// </summary>
/// <remarks>
/// Typed on purpose. Every member is an identifier, a time, an enumeration, a number or a flag; there is no text and
/// no property bag, so no title, description, price, evidence or any other submitted content can reach the audit
/// trail by accident, and the record means the same thing however it is stored. The ids are plain values with no
/// relationship to the deleted rows, so the record outlives them.
/// </remarks>
public sealed record ListingDraftDiscardedAuditEvent
{
    public ListingDraftDiscardedAuditEvent(
        Guid eventId,
        DateTimeOffset occurredAt,
        Guid actorUserId,
        Guid submissionId,
        Guid listingId,
        Guid propertyId,
        ListingPurpose purpose,
        int version,
        bool listingDeleted)
    {
        if (eventId == Guid.Empty
            || actorUserId == Guid.Empty
            || submissionId == Guid.Empty
            || listingId == Guid.Empty
            || propertyId == Guid.Empty)
        {
            throw new ArgumentException("Audit identifiers must not be empty.");
        }

        if (occurredAt == default)
        {
            throw new ArgumentException("The audit time is required.", nameof(occurredAt));
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        EventId = eventId;
        OccurredAt = occurredAt;
        ActorUserId = actorUserId;
        SubmissionId = submissionId;
        ListingId = listingId;
        PropertyId = propertyId;
        Purpose = purpose;
        Version = version;
        ListingDeleted = listingDeleted;
    }

    public Guid EventId { get; }

    public DateTimeOffset OccurredAt { get; }

    public Guid ActorUserId { get; }

    public Guid SubmissionId { get; }

    public Guid ListingId { get; }

    public Guid PropertyId { get; }

    public ListingPurpose Purpose { get; }

    public int Version { get; }

    public bool ListingDeleted { get; }
}
