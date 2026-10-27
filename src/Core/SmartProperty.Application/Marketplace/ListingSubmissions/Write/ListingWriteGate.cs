using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// What the gate learned: either the error to return, or the locked property and the facts a verified grant gives.
/// </summary>
internal sealed record ListingWriteGateResult(
    Error? Error,
    LockedPropertyView? Property,
    ListingAuthorityFacts? Authority,
    SubmissionVerificationFacts? SubmissionFacts)
{
    public bool IsOpen => Error is null;

    public static ListingWriteGateResult Closed(Error error)
    {
        return new ListingWriteGateResult(error, null, null, null);
    }
}

/// <summary>
/// The one place a listing write is allowed to start: lock the property, then read and evaluate the grant under that
/// lock, and only then look at the property itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order.</b> Property row first, grant row shared second, which is the order the repository contract and
/// every other writer use. The listing and its submissions are locked afterwards by the caller.
/// </para>
/// <para>
/// <b>The grant is evaluated once, under the locks, and that evaluation is the decision.</b> There is no earlier,
/// lock-free evaluation to go stale, and no result from before the lock is ever consulted. A missing property, a
/// missing grant, and every way a grant can be unsuitable are the same <see cref="WriteErrors.NotEligible"/>, so a
/// caller cannot tell an unknown property from one they may not list.
/// </para>
/// <para>
/// <b>The property is examined only after eligibility.</b> An ineligible caller never learns whether a property is
/// archived, reserved or incomplete. An eligible one is told with the single <see cref="WriteErrors.PropertyUnavailable"/>.
/// </para>
/// </remarks>
internal static class ListingWriteGate
{
    public static async Task<ListingWriteGateResult> EvaluateAsync(
        IListingSubmissionWriteRepository repository,
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var property = await repository.LockPropertyAsync(propertyId, cancellationToken);

        // The repository must answer for the property asked about. Anything else is treated as no property at all.
        if (property is null || property.PropertyId != propertyId)
        {
            return ListingWriteGateResult.Closed(WriteErrors.NotEligible);
        }

        var grant = await repository.GetGrantForSharedLockAsync(actingUserId, propertyId, purpose, cancellationToken);
        var eligibility = GrantEligibilityEvaluator.Evaluate(grant, actingUserId, propertyId, purpose, at);

        if (!eligibility.IsEligible)
        {
            return ListingWriteGateResult.Closed(WriteErrors.NotEligible);
        }

        if (!property.IsActive || property.HasMarketplaceReservation)
        {
            return ListingWriteGateResult.Closed(WriteErrors.PropertyUnavailable);
        }

        return new ListingWriteGateResult(null, property, eligibility.Authority, eligibility.SubmissionFacts);
    }
}
