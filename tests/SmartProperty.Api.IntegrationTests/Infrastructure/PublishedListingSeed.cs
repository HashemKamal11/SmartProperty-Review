using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Writes an already published advertisement for a property straight into the database through the Domain, for tests
/// about something else (search mapping, reads) that need a visible listing without running the whole workflow.
/// </summary>
internal static class PublishedListingSeed
{
    public static async Task<Guid> PublishAsync(
        ApiPostgreSqlFixture fixture,
        Guid propertyId,
        ListingPurpose purpose = ListingPurpose.Sale)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        var owner = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var listing = new PropertyListing(Guid.NewGuid(), propertyId, purpose, at);
        context.Add(listing);
        await context.SaveChangesAsync();

        var authority = new ListingLegalAuthority(propertyId, purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), owner), owner,
            ListingRepresentationKind.Owner, null, at, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), propertyId, purpose, new string('A', 64));
        var content = new ListingSubmissionContent("Egypt", "Cairo", "Cairo", "Maadi", "A listing", "A description",
            PropertyType.Apartment, purpose, 100m, "EGP", purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at.AddSeconds(1), null);
        var submittedAt = at.AddSeconds(2);
        submission.Submit(owner, null, submittedAt, Assessments(submission, submittedAt), true, null);
        context.Add(submission);
        await context.SaveChangesAsync();

        var decidedAt = at.AddSeconds(3);
        var reviewer = new ListingReviewContext(Guid.NewGuid(), owner, submission.Authority.DeclaredOwner, null, true, true, null, false);
        submission.ApproveAndPublish(Guid.NewGuid(), listing, null, reviewer, decidedAt, true, Assessments(submission, decidedAt), true, null);
        await context.SaveChangesAsync();

        return listing.Id;
    }

    private static ListingEvidenceAssessment[] Assessments(ListingSubmission submission, DateTimeOffset at) =>
        submission.Evidence.Select(e => new ListingEvidenceAssessment(Guid.NewGuid(), e, EvidenceScanStatus.Clean, at)).ToArray();
}
