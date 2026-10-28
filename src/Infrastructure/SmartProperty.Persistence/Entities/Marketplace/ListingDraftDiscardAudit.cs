using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Persistence.Entities.Marketplace;

/// <summary>
/// Private persistence fact, never a public response. Preparing it does not authorize or perform a discard.
/// Saving remains blocked until the grant-checked write transaction can pair it with the exact deletion.
/// Identifiers intentionally have no FK to rows whose deletion must not erase audit history.
/// </summary>
public sealed class ListingDraftDiscardAudit
{
    private ListingDraftDiscardAudit() { }

    public Guid EventId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid ActorUserId { get; private set; }
    public Guid SubmissionId { get; private set; }
    public Guid ListingId { get; private set; }
    public Guid PropertyId { get; private set; }
    public ListingPurpose Purpose { get; private set; }
    public int Version { get; private set; }
    public int Revision { get; private set; }
    public bool ListingDeleted { get; private set; }

    public static ListingDraftDiscardAudit Prepare(ListingSubmission draft, int expectedRevision,
        ListingDraftDiscardedAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(auditEvent);
        draft.EnsureCanDiscard(expectedRevision);
        if (auditEvent.SubmissionId != draft.Id || auditEvent.ListingId != draft.ListingId
            || auditEvent.PropertyId != draft.PropertyId || auditEvent.Purpose != draft.Purpose
            || auditEvent.Version != draft.Version || auditEvent.ActorUserId != draft.Authority.ActingUserId
            || auditEvent.OccurredAt < draft.UpdatedAt)
            throw new InvalidOperationException("Discard audit must describe the exact current owned draft.");
        return new ListingDraftDiscardAudit
        {
            EventId = auditEvent.EventId, OccurredAt = auditEvent.OccurredAt,
            ActorUserId = auditEvent.ActorUserId, SubmissionId = draft.Id,
            ListingId = draft.ListingId, PropertyId = draft.PropertyId, Purpose = draft.Purpose,
            Version = draft.Version, Revision = draft.Revision, ListingDeleted = auditEvent.ListingDeleted
        };
    }
}
