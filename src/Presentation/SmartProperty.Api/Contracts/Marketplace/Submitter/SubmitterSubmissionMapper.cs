#nullable enable
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Contracts.Marketplace.Submitter;

/// <summary>
/// Maps the Application submitter read models to the submitter response contracts.
/// </summary>
/// <remarks>
/// A pure function. It copies the fields it is given and draws no conclusion of its own: it takes no user, decides
/// nothing about legal ownership, account status or authorization, and never learns whose submission it is.
///
/// It does refuse a read model that contradicts an invariant the Domain itself enforces, because serving such a
/// record would mean the repository is returning corrupt data: an undefined enumeration, a rent period on a sale or
/// none on a rental, a Draft with a submission time, a reviewed submission without one, timestamps out of order,
/// "currently published" on something that is not approved, or a decision that does not fit the status. A Draft is
/// valid here, because it is the submitter's own. Refusal throws an <see cref="InvalidOperationException"/> that
/// names the fault and nothing from the record, so the response is the generic server error and no identifier, text
/// or value is disclosed.
/// </remarks>
internal static class SubmitterSubmissionMapper
{
    public static SubmitterSubmissionSummaryResponse ToSummaryResponse(SubmitterSubmissionSummary submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        RequireCommon(
            submission.SubmissionId,
            submission.ListingId,
            submission.Status,
            submission.Version,
            submission.CreatedAt,
            submission.SubmittedAt,
            submission.UpdatedAt,
            submission.IsCurrentlyPublished);

        return new SubmitterSubmissionSummaryResponse(
            submission.SubmissionId,
            submission.ListingId,
            ToPurpose(submission.Purpose),
            submission.Version,
            ToStatus(submission.Status),
            RequireText(submission.Title, "title"),
            ToPrice(submission.Price, submission.Purpose),
            ToPropertyType(submission.PropertyType),
            ToLocation(submission.Location),
            ToRepresentation(submission.Representation),
            submission.CreatedAt,
            submission.SubmittedAt,
            submission.UpdatedAt,
            submission.IsCurrentlyPublished);
    }

    public static SubmitterSubmissionDetailResponse ToDetailResponse(SubmitterSubmissionDetail submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        RequireCommon(
            submission.SubmissionId,
            submission.ListingId,
            submission.Status,
            submission.Version,
            submission.CreatedAt,
            submission.SubmittedAt,
            submission.UpdatedAt,
            submission.IsCurrentlyPublished);

        return new SubmitterSubmissionDetailResponse(
            submission.SubmissionId,
            submission.ListingId,
            ToPurpose(submission.Purpose),
            submission.Version,
            ToStatus(submission.Status),
            RequireText(submission.Title, "title"),
            RequireText(submission.Description, "description"),
            ToPrice(submission.Price, submission.Purpose),
            ToPropertyType(submission.PropertyType),
            ToLocation(submission.Location),
            ToRepresentation(submission.Representation),
            submission.CreatedAt,
            submission.SubmittedAt,
            submission.UpdatedAt,
            submission.IsCurrentlyPublished,
            ToDecision(submission));
    }

    public static PagedResponse<SubmitterSubmissionSummaryResponse> ToPagedResponse(
        PagedList<SubmitterSubmissionSummary> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var items = page.Items.Select(ToSummaryResponse).ToArray();

        // Re-paged rather than hand-assembled, so the response's page metadata comes from the same
        // PagedList/PagedResponse pair every other collection endpoint uses.
        return PagedResponse<SubmitterSubmissionSummaryResponse>.FromPagedList(
            PagedList<SubmitterSubmissionSummaryResponse>.Create(
                items,
                page.PageNumber,
                page.PageSize,
                page.TotalCount));
    }

    // A Draft has never been submitted. Every other status has been, at a time between creation and the last update.
    private static void RequireCommon(
        Guid submissionId,
        Guid listingId,
        ListingSubmissionStatus status,
        int version,
        DateTimeOffset createdAt,
        DateTimeOffset? submittedAt,
        DateTimeOffset updatedAt,
        bool isCurrentlyPublished)
    {
        RequireId(submissionId, "submission id");
        RequireId(listingId, "listing id");

        if (!Enum.IsDefined(status))
        {
            throw Invalid("an unsupported status");
        }

        if (version < 1)
        {
            throw Invalid("a version below one");
        }

        RequireTime(createdAt, "creation time");
        RequireTime(updatedAt, "update time");

        if (updatedAt < createdAt)
        {
            throw Invalid("timestamps out of order");
        }

        if (status == ListingSubmissionStatus.Draft)
        {
            if (submittedAt is not null)
            {
                throw Invalid("a submission time on a draft");
            }
        }
        else
        {
            if (submittedAt is not { } submitted)
            {
                throw Invalid("a submitted submission without a submission time");
            }

            RequireTime(submitted, "submission time");

            if (submitted < createdAt || updatedAt < submitted)
            {
                throw Invalid("timestamps out of order");
            }
        }

        if (isCurrentlyPublished && status != ListingSubmissionStatus.Approved)
        {
            throw Invalid("a published submission that is not approved");
        }
    }

    // Nothing has been decided for a Draft or a pending submission. Approved and Superseded submissions carry an
    // approval (a superseded one was approved before its successor replaced it) and never a reason. A rejected one
    // carries a rejection with its reason, which is the submitter's to read. The decision falls between submission
    // and the last update.
    private static SubmitterSubmissionDecisionResponse? ToDecision(SubmitterSubmissionDetail submission)
    {
        var decision = submission.Decision;

        if (submission.Status is ListingSubmissionStatus.Draft or ListingSubmissionStatus.PendingReview)
        {
            return decision is null ? null : throw Invalid("a decision on an undecided submission");
        }

        if (decision is null)
        {
            throw Invalid("a reviewed submission without a decision");
        }

        // RequireCommon guarantees a submission time for every status that reaches this point.
        if (decision.DecidedAt < submission.SubmittedAt || decision.DecidedAt > submission.UpdatedAt)
        {
            throw Invalid("a decision time outside the lifetime of the submission");
        }

        if (submission.Status == ListingSubmissionStatus.Rejected)
        {
            if (decision.Approved)
            {
                throw Invalid("an approval on a rejected submission");
            }

            return new SubmitterSubmissionDecisionResponse(
                Approved: false,
                RequireText(decision.Reason, "rejection reason"),
                decision.DecidedAt);
        }

        if (!decision.Approved)
        {
            throw Invalid("a rejection on an approved submission");
        }

        // An approval carries no reason; whatever may be stored is never shown.
        return new SubmitterSubmissionDecisionResponse(Approved: true, Reason: null, decision.DecidedAt);
    }

    private static string ToStatus(ListingSubmissionStatus status)
    {
        return status switch
        {
            ListingSubmissionStatus.Draft => "Draft",
            ListingSubmissionStatus.PendingReview => "PendingReview",
            ListingSubmissionStatus.Approved => "Approved",
            ListingSubmissionStatus.Rejected => "Rejected",
            ListingSubmissionStatus.Superseded => "Superseded",
            _ => throw Invalid("an unsupported status")
        };
    }

    private static string ToPurpose(ListingPurpose purpose)
    {
        return purpose switch
        {
            ListingPurpose.Sale => "Sale",
            ListingPurpose.Rent => "Rent",
            _ => throw Invalid("an unsupported listing purpose")
        };
    }

    private static string ToPropertyType(PropertyType propertyType)
    {
        return propertyType switch
        {
            PropertyType.Land => "Land",
            PropertyType.Building => "Building",
            PropertyType.Unit => "Unit",
            _ => throw Invalid("an unsupported property type")
        };
    }

    private static string ToRepresentation(ListingRepresentationKind representation)
    {
        return representation switch
        {
            ListingRepresentationKind.Owner => "Owner",
            ListingRepresentationKind.IndividualRepresentative => "IndividualRepresentative",
            ListingRepresentationKind.WorkspaceRepresentative => "WorkspaceRepresentative",
            _ => throw Invalid("an unsupported representation")
        };
    }

    private static string ToRentPeriod(RentPeriod rentPeriod)
    {
        return rentPeriod switch
        {
            RentPeriod.Monthly => "Monthly",
            RentPeriod.Yearly => "Yearly",
            _ => throw Invalid("an unsupported rent period")
        };
    }

    private static PublicMoneyResponse ToPrice(PublicListingPrice? price, ListingPurpose purpose)
    {
        if (price is null)
        {
            throw Invalid("a missing price");
        }

        var currency = RequireText(price.Currency, "currency");

        return purpose switch
        {
            ListingPurpose.Sale when price.RentPeriod is null =>
                new PublicMoneyResponse(price.Amount, currency),
            ListingPurpose.Sale =>
                throw Invalid("a rent period on a sale"),
            ListingPurpose.Rent when price.RentPeriod is { } rentPeriod =>
                new PublicMoneyResponse(price.Amount, currency, ToRentPeriod(rentPeriod)),
            ListingPurpose.Rent =>
                throw Invalid("a rental without a rent period"),
            _ => throw Invalid("an unsupported listing purpose")
        };
    }

    private static PublicLocationResponse ToLocation(PublicListingLocation? location)
    {
        if (location is null)
        {
            throw Invalid("a missing location");
        }

        return new PublicLocationResponse(
            RequireText(location.CountryCode, "country"),
            RequireText(location.City, "city"),
            RequireText(location.Region, "region"),
            RequireText(location.District, "district"));
    }

    private static void RequireId(Guid id, string field)
    {
        if (id == Guid.Empty)
        {
            throw Invalid($"an empty {field}");
        }
    }

    private static void RequireTime(DateTimeOffset value, string field)
    {
        if (value == default)
        {
            throw Invalid($"a missing {field}");
        }
    }

    private static string RequireText(string? value, string field)
    {
        return string.IsNullOrWhiteSpace(value) ? throw Invalid($"a blank {field}") : value;
    }

    private static InvalidOperationException Invalid(string fault)
    {
        return new InvalidOperationException($"A submitter read model is invalid: {fault}.");
    }
}
