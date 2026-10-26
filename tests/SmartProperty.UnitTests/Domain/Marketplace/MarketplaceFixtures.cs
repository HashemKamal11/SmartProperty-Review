using SmartProperty.Domain.Marketplace;

namespace SmartProperty.UnitTests.Domain.Marketplace;

internal static class MarketplaceFixtures
{
    internal static readonly DateTimeOffset CreatedAt = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset PublishedAt = CreatedAt.AddHours(1);
    internal static readonly DateTimeOffset RequestedAt = PublishedAt.AddHours(1);
    internal static readonly DateTimeOffset DecidedAt = RequestedAt.AddHours(1);

    internal static PropertyListing CreateListing(
        ListingPurpose purpose,
        Guid? propertyId = null,
        Guid? listingId = null,
        bool publish = true)
    {
        var listing = new PropertyListing(
            listingId ?? Guid.NewGuid(),
            propertyId ?? Guid.NewGuid(),
            purpose,
            CreatedAt);

        if (publish)
        {
            Approve(listing, PublishedAt);
        }

        return listing;
    }

    internal static ListingSubmission Pending(PropertyListing listing, DateTimeOffset at, ListingSubmission? predecessor = null)
    {
        var actor = Guid.NewGuid();
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor), actor,
            ListingRepresentationKind.Owner, null, listing.CreatedAt, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64));
        var content = new ListingSubmissionContent("Country", "Region", "City", "District", "Title", "Description",
            SmartProperty.Domain.PropertyRegistry.PropertyType.Unit, listing.Purpose, 100m, "USD",
            listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at, predecessor);
        submission.Submit(actor, null, at, Assessments(submission, at), true, null);
        return submission;
    }

    internal static ListingReviewContext Reviewer(ListingSubmission submission) => new(Guid.NewGuid(),
        submission.Authority.ActingUserId, submission.Authority.DeclaredOwner, submission.Authority.WorkspaceId,
        true, true, submission.Authority.WorkspaceId.HasValue ? false : null, false);

    internal static ListingEvidenceAssessment[] Assessments(ListingSubmission submission, DateTimeOffset at) =>
        submission.Evidence.Select(e => new ListingEvidenceAssessment(Guid.NewGuid(), e, EvidenceScanStatus.Clean, at)).ToArray();

    internal static ListingSubmission Approve(PropertyListing listing, DateTimeOffset at, ListingSubmission? predecessor = null)
    {
        var submission = Pending(listing, at, predecessor);
        submission.ApproveAndPublish(Guid.NewGuid(), listing, predecessor, Reviewer(submission), at, true,
            Assessments(submission, at), true, null);
        return submission;
    }
    internal static PurchaseRequest CreatePurchaseRequest(PropertyListing listing, Guid? id = null)
    {
        return new PurchaseRequest(id ?? Guid.NewGuid(), listing, Guid.NewGuid(), RequestedAt);
    }

    internal static RentalRequest CreateRentalRequest(PropertyListing listing, Guid? id = null)
    {
        return new RentalRequest(id ?? Guid.NewGuid(), listing, Guid.NewGuid(), RequestedAt);
    }
}
