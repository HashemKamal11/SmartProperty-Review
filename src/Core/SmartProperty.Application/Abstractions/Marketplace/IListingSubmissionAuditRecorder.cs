namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// Records typed audit events for listing submission writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording is not committing.</b> An implementation adds the event to the same unit of work as the write it
/// describes and does not save. The write repository's single <c>SaveChangesAsync</c> then commits the change and its
/// audit event in one database transaction, so a discard without its audit record, or an audit record of a discard
/// that did not happen, cannot exist. An implementation that flushed on its own would break that.
/// </para>
/// <para>
/// Only typed events are accepted. There is no method that takes free text or a property bag.
/// </para>
/// </remarks>
public interface IListingSubmissionAuditRecorder
{
    Task RecordAsync(ListingDraftDiscardedAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
