#nullable enable
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.Contracts.Marketplace.Admin;

/// <summary>
/// Maps the Application review read models to the Platform Admin response contracts.
/// </summary>
/// <remarks>
/// A pure function. It copies the fields it is given and draws no conclusion of its own: in particular it decides
/// nothing about reviewer independence or ownership, which the trusted repository has already resolved by the time
/// a read model exists.
///
/// It does refuse a read model that contradicts an invariant the Domain itself enforces, because serving such a
/// record would mean the repository is returning corrupt data: an undefined enumeration, a Draft (never reviewable),
/// a first version with a predecessor or a later version without one, a rent period on a sale or none on a rental,
/// an Owner representation of an Organization, a validity window that ends before it starts, timestamps out of
/// order, no evidence, or a decision that does not fit the status. Refusal throws an
/// <see cref="InvalidOperationException"/> that names the fault and nothing from the record, so the response is the
/// generic server error and no identifier, text or value is disclosed.
/// </remarks>
internal static class AdminReviewMapper
{
    public static ReviewQueueItemResponse ToQueueItemResponse(ReviewQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        RequireId(item.SubmissionId, "submission id");
        RequireId(item.ListingId, "listing id");
        RequireReviewableStatus(item.Status);
        RequirePositive(item.Version, "version");
        RequireEvidence(item.EvidenceCount);
        RequireTime(item.SubmittedAt, "submission time");
        RequireRepresentation(item.Representation, item.OwnerKind);

        return new ReviewQueueItemResponse(
            item.SubmissionId,
            item.ListingId,
            ToPurpose(item.Purpose),
            item.Version,
            ToStatus(item.Status),
            RequireText(item.Title, "title"),
            ToPrice(item.Price, item.Purpose),
            ToPropertyType(item.PropertyType),
            ToLocation(item.Location),
            ToRepresentation(item.Representation),
            ToOwnerKind(item.OwnerKind),
            item.SubmittedAt,
            item.EvidenceCount,
            item.AllEvidenceClean);
    }

    public static ReviewSubmissionDetailResponse ToDetailResponse(ReviewSubmissionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        RequireId(detail.SubmissionId, "submission id");
        RequireId(detail.ListingId, "listing id");
        RequireId(detail.PropertyId, "property id");
        RequireReviewableStatus(detail.Status);
        RequirePositive(detail.Version, "version");
        RequireEvidence(detail.EvidenceCount);
        RequireTime(detail.CreatedAt, "creation time");
        RequireTime(detail.SubmittedAt, "submission time");
        RequireTime(detail.UpdatedAt, "update time");
        RequireTime(detail.AuthorityValidFrom, "authority start");
        RequireRepresentation(detail.Representation, detail.OwnerKind);
        RequirePredecessor(detail);

        if (detail.AuthorityValidUntil is { } validUntil && validUntil <= detail.AuthorityValidFrom)
        {
            throw Invalid("an authority window that does not end after it starts");
        }

        if (detail.SubmittedAt < detail.CreatedAt || detail.UpdatedAt < detail.SubmittedAt)
        {
            throw Invalid("timestamps out of order");
        }

        if (detail.IsCurrentlyPublished && detail.Status != ListingSubmissionStatus.Approved)
        {
            throw Invalid("a published submission that is not approved");
        }

        return new ReviewSubmissionDetailResponse(
            detail.SubmissionId,
            detail.ListingId,
            detail.PropertyId,
            detail.PredecessorSubmissionId,
            ToPurpose(detail.Purpose),
            detail.Version,
            ToStatus(detail.Status),
            RequireText(detail.Title, "title"),
            RequireText(detail.Description, "description"),
            ToPrice(detail.Price, detail.Purpose),
            ToPropertyType(detail.PropertyType),
            ToLocation(detail.Location),
            ToRepresentation(detail.Representation),
            ToOwnerKind(detail.OwnerKind),
            detail.AuthorityValidFrom,
            detail.AuthorityValidUntil,
            detail.CreatedAt,
            detail.SubmittedAt,
            detail.UpdatedAt,
            detail.EvidenceCount,
            detail.AllEvidenceClean,
            detail.IsCurrentlyPublished,
            ToDecision(detail));
    }

    // Version 1 has no predecessor; every later version has one, and it is never the submission itself.
    private static void RequirePredecessor(ReviewSubmissionDetail detail)
    {
        if (detail.Version == 1)
        {
            if (detail.PredecessorSubmissionId is not null)
            {
                throw Invalid("a predecessor on the first version");
            }

            return;
        }

        if (detail.PredecessorSubmissionId is not { } predecessor
            || predecessor == Guid.Empty
            || predecessor == detail.SubmissionId)
        {
            throw Invalid("a later version without a valid predecessor");
        }
    }

    // A pending submission has no decision. Approved and Superseded submissions carry an approval (a superseded one
    // was approved before its successor replaced it). A rejected one carries a rejection with its reason. The
    // decision falls between submission and the last update.
    private static ReviewDecisionResponse? ToDecision(ReviewSubmissionDetail detail)
    {
        var decision = detail.Decision;

        if (detail.Status == ListingSubmissionStatus.PendingReview)
        {
            return decision is null ? null : throw Invalid("a decision on a pending submission");
        }

        if (decision is null)
        {
            throw Invalid("a reviewed submission without a decision");
        }

        if (decision.DecidedAt < detail.SubmittedAt || decision.DecidedAt > detail.UpdatedAt)
        {
            throw Invalid("a decision time outside the submission's lifetime");
        }

        if (detail.Status == ListingSubmissionStatus.Rejected)
        {
            if (decision.Approved)
            {
                throw Invalid("an approval on a rejected submission");
            }

            return new ReviewDecisionResponse(
                Approved: false,
                RequireText(decision.Reason, "rejection reason"),
                decision.DecidedAt);
        }

        if (!decision.Approved)
        {
            throw Invalid("a rejection on an approved submission");
        }

        // An approval carries no reason; whatever may be stored is never shown.
        return new ReviewDecisionResponse(Approved: true, Reason: null, decision.DecidedAt);
    }

    private static void RequireReviewableStatus(ListingSubmissionStatus status)
    {
        if (status is not (ListingSubmissionStatus.PendingReview
            or ListingSubmissionStatus.Approved
            or ListingSubmissionStatus.Rejected
            or ListingSubmissionStatus.Superseded))
        {
            throw Invalid("a status that is not reviewable");
        }
    }

    // The Domain lets only a representative act for an Organization.
    private static void RequireRepresentation(ListingRepresentationKind representation, ListingOwnerKind ownerKind)
    {
        if (representation == ListingRepresentationKind.Owner && ownerKind == ListingOwnerKind.Organization)
        {
            throw Invalid("an Organization acting as its own owner");
        }
    }

    private static string ToStatus(ListingSubmissionStatus status)
    {
        return status switch
        {
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

    private static string ToOwnerKind(ListingOwnerKind ownerKind)
    {
        return ownerKind switch
        {
            ListingOwnerKind.Individual => "Individual",
            ListingOwnerKind.Organization => "Organization",
            _ => throw Invalid("an unsupported owner kind")
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

    private static void RequirePositive(int value, string field)
    {
        if (value < 1)
        {
            throw Invalid($"a {field} below one");
        }
    }

    // The Domain requires private evidence on every submission, so a read model with none is not a real one.
    private static void RequireEvidence(int evidenceCount)
    {
        if (evidenceCount < 1)
        {
            throw Invalid("a submission without evidence");
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
        return new InvalidOperationException($"A review read model is invalid: {fault}.");
    }
}
