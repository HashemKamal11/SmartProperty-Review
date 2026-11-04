using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Separately gated: this class starts the disposable PostgreSQL fixture.
public sealed class ReviewerListingSubmissionReadRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Sentinel = "SENTINEL-REASON passport 123456789";
    private static readonly DateTimeOffset T0 = MarketplaceGraph.Time;
    private ServiceProvider? services;

    public enum State { Draft, Pending, Approved, Rejected }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        services = new ServiceCollection().AddPersistence(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Host.ConnectionString })
            .Build()).BuildServiceProvider();
    }

    public override async Task DisposeAsync()
    {
        services?.Dispose();
        await base.DisposeAsync();
    }

    // ----- registration

    [Fact]
    public void TheRepositoryIsRegisteredScopedAndNothingElseOfTheReviewReadStackIs()
    {
        using var scope = services!.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<IReviewerListingSubmissionReadRepository>();
        var second = scope.ServiceProvider.GetRequiredService<IReviewerListingSubmissionReadRepository>();
        Assert.Same(first, second);
        using var other = services!.CreateScope();
        Assert.NotSame(first, other.ServiceProvider.GetRequiredService<IReviewerListingSubmissionReadRepository>());

        Assert.Null(scope.ServiceProvider.GetService<SmartProperty.Application.Abstractions.Authorization.IReviewReadAuthorizer>());
        // The decision authorizer is excluded from this build, so it is named, not referenced: if it exists it must not
        // be resolvable here. The anchor proves the lookup searches the assembly that would define it.
        var application = typeof(SmartProperty.Application.Abstractions.Authorization.IReviewReadAuthorizer).Assembly;
        Assert.NotNull(application.GetType("SmartProperty.Application.Abstractions.Authorization.IReviewReadAuthorizer", throwOnError: false));
        if (application.GetType("SmartProperty.Application.Abstractions.Authorization.IReviewDecisionAuthorizer", throwOnError: false) is { } decisionAuthorizer)
        {
            Assert.Null(scope.ServiceProvider.GetService(decisionAuthorizer));
        }
    }

    // ----- queue

    [Fact]
    public async Task ThePendingQueueHoldsOnlyPendingSubmissionsAndNeverADraft()
    {
        var pendingA = await SeedAsync(State.Pending, minute: 1);
        var pendingB = await SeedAsync(State.Pending, minute: 2);
        await SeedAsync(State.Draft, minute: 3);
        await SeedAsync(State.Approved, minute: 4);
        await SeedAsync(State.Rejected, minute: 5);

        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10));

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { pendingA.Id, pendingB.Id }, page.Items.Select(item => item.SubmissionId).ToArray());
        Assert.All(page.Items, item => Assert.Equal(ListingSubmissionStatus.PendingReview, item.Status));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.Draft)]
    [InlineData((ListingSubmissionStatus)99)]
    public async Task ADraftOrUndefinedStatusFilterIsRejected(ListingSubmissionStatus status)
    {
        await SeedAsync(State.Draft);

        await Assert.ThrowsAsync<ArgumentException>(() => Read(r => r.ListForReviewAsync(null, status, 0, 10)));
    }

    [Fact]
    public async Task EachNonDraftStatusIsListedByItself()
    {
        var approved = await SeedAsync(State.Approved);
        var rejected = await SeedAsync(State.Rejected);

        var approvedPage = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.Approved, 0, 10));
        var rejectedPage = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.Rejected, 0, 10));

        Assert.Equal(approved.Id, Assert.Single(approvedPage.Items).SubmissionId);
        Assert.Equal(rejected.Id, Assert.Single(rejectedPage.Items).SubmissionId);
        Assert.Empty((await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.Superseded, 0, 10))).Items);
    }

    [Fact]
    public async Task ThePurposeFilterNarrowsTheItemsAndTheCountBeforePaging()
    {
        await SeedAsync(State.Pending, ListingPurpose.Sale, minute: 1);
        var rent = await SeedAsync(State.Pending, ListingPurpose.Rent, minute: 2);
        await SeedAsync(State.Pending, ListingPurpose.Sale, minute: 3);

        var page = await Read(r => r.ListForReviewAsync(ListingPurpose.Rent, ListingSubmissionStatus.PendingReview, 0, 1));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(rent.Id, Assert.Single(page.Items).SubmissionId);
        await Assert.ThrowsAsync<ArgumentException>(
            () => Read(r => r.ListForReviewAsync((ListingPurpose)99, ListingSubmissionStatus.PendingReview, 0, 1)));
    }

    [Fact]
    public async Task ThePendingQueueIsOldestSubmittedFirstWithTiesBrokenByIdAscending()
    {
        var late = await SeedAsync(State.Pending, minute: 9);
        var tiedA = await SeedAsync(State.Pending, minute: 5);
        var tiedB = await SeedAsync(State.Pending, minute: 5);
        var tiedC = await SeedAsync(State.Pending, minute: 5);
        var early = await SeedAsync(State.Pending, minute: 1);

        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10));

        var tied = new[] { tiedA.Id, tiedB.Id, tiedC.Id }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal);
        Assert.Equal(new[] { early.Id }.Concat(tied).Append(late.Id).ToArray(), page.Items.Select(item => item.SubmissionId).ToArray());
    }

    [Fact]
    public async Task OtherStatusesAreMostRecentlyUpdatedFirstWithTiesBrokenByIdAscending()
    {
        var oldest = await SeedAsync(State.Approved, minute: 1);
        var tiedA = await SeedAsync(State.Approved, minute: 5);
        var tiedB = await SeedAsync(State.Approved, minute: 5);
        var newest = await SeedAsync(State.Approved, minute: 9);

        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.Approved, 0, 10));

        var tied = new[] { tiedA.Id, tiedB.Id }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal);
        Assert.Equal(new[] { newest.Id }.Concat(tied).Append(oldest.Id).ToArray(), page.Items.Select(item => item.SubmissionId).ToArray());
    }

    [Fact]
    public async Task PagesPartitionTheQueueWithoutRepeatsOrGapsAndTheCountIsTheFullMatchCount()
    {
        var all = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            all.Add((await SeedAsync(State.Pending, minute: i + 1)).Id);
        }

        var seen = new List<Guid>();
        foreach (var skip in new[] { 0, 2, 4 })
        {
            var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, skip, 2));
            Assert.Equal(5, page.TotalCount);
            seen.AddRange(page.Items.Select(item => item.SubmissionId));
        }

        Assert.Equal(all, seen);
        Assert.Empty((await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 5, 2))).Items);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, -1, 2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, int.MaxValue)));
    }

    [Fact]
    public async Task AnEmptyQueueIsAnEmptyPageNotAnError()
    {
        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    // ----- detail

    [Fact]
    public async Task APendingDetailCarriesTheApprovedFieldsAndNoDecision()
    {
        var submission = await SeedAsync(State.Pending, ListingPurpose.Rent, minute: 3, title: "Bright flat", evidence: 2);

        var lookup = await Read(r => r.GetForReviewAsync(submission.Id));

        Assert.Equal(ReviewDetailOutcome.Found, lookup.Outcome);
        var detail = lookup.Detail!;
        Assert.Equal(submission.Id, detail.SubmissionId);
        Assert.Equal(submission.ListingId, detail.ListingId);
        Assert.Equal(submission.PropertyId, detail.PropertyId);
        Assert.Null(detail.PredecessorSubmissionId);
        Assert.Equal(ListingPurpose.Rent, detail.Purpose);
        Assert.Equal(1, detail.Version);
        Assert.Equal(ListingSubmissionStatus.PendingReview, detail.Status);
        Assert.Equal("Bright flat", detail.Title);
        Assert.Equal("Description", detail.Description);
        Assert.Equal(100m, detail.Price.Amount);
        Assert.Equal("USD", detail.Price.Currency);
        Assert.Equal(RentPeriod.Monthly, detail.Price.RentPeriod);
        Assert.Equal(PropertyType.Unit, detail.PropertyType);
        Assert.Equal(("Country", "Region", "City", "District"), (detail.Location.CountryCode, detail.Location.Region, detail.Location.City, detail.Location.District));
        Assert.Equal(ListingRepresentationKind.Owner, detail.Representation);
        Assert.Equal(ListingOwnerKind.Individual, detail.OwnerKind);
        Assert.Equal(submission.Authority.ValidFrom, detail.AuthorityValidFrom);
        Assert.Null(detail.AuthorityValidUntil);
        Assert.Equal(submission.SubmittedAt, detail.SubmittedAt);
        Assert.Equal(2, detail.EvidenceCount);
        Assert.True(detail.AllEvidenceClean);
        Assert.False(detail.IsCurrentlyPublished);
        Assert.Null(detail.Decision);
    }

    [Fact]
    public async Task ASaleHasNoRentPeriodAndABoundedAuthorityShowsItsEnd()
    {
        var submission = await SeedAsync(State.Pending, ListingPurpose.Sale);
        await using (var context = Host.CreateVerificationContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.submissions SET authority_snapshot = jsonb_set(authority_snapshot, ARRAY['Payload','ValidUntil'], to_jsonb('2027-01-01T00:00:00+00:00'::text)) WHERE id = {0}",
                submission.Id);
        }

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;

        Assert.Null(detail.Price.RentPeriod);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), detail.AuthorityValidUntil);
    }

    [Fact]
    public async Task AnApprovedPublishedDetailShowsTheApprovalWithoutAReasonAndIsCurrentlyPublished()
    {
        var submission = await SeedAsync(State.Approved);

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;

        Assert.Equal(ListingSubmissionStatus.Approved, detail.Status);
        Assert.True(detail.IsCurrentlyPublished);
        Assert.True(detail.Decision!.Approved);
        Assert.Null(detail.Decision.Reason);
        Assert.Equal(submission.ReviewDecision!.DecidedAt, detail.Decision.DecidedAt);
    }

    [Fact]
    public async Task ASupersededDetailKeepsItsApprovalIsNoLongerPublishedAndItsSuccessorNamesIt()
    {
        var (first, second) = await SeedSupersededAsync();

        var superseded = (await Read(r => r.GetForReviewAsync(first.Id))).Detail!;
        var successor = (await Read(r => r.GetForReviewAsync(second.Id))).Detail!;

        Assert.Equal(ListingSubmissionStatus.Superseded, superseded.Status);
        Assert.False(superseded.IsCurrentlyPublished);
        Assert.True(superseded.Decision!.Approved);
        Assert.Equal(ListingSubmissionStatus.Approved, successor.Status);
        Assert.True(successor.IsCurrentlyPublished);
        Assert.Equal(first.Id, successor.PredecessorSubmissionId);
        Assert.Equal(2, successor.Version);
    }

    [Fact]
    public async Task ARejectionShowsOnlyItsOutcomeAndTimeEvenThoughAReasonIsStored()
    {
        var submission = await SeedAsync(State.Rejected, reason: Sentinel);

        await using (var context = Host.CreateVerificationContext())
        {
            var stored = await context.Database
                .SqlQueryRaw<string>("SELECT reason AS \"Value\" FROM marketplace.review_decisions WHERE submission_id = {0}", submission.Id)
                .SingleAsync();
            Assert.Equal(Sentinel, stored);
        }

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;
        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.Rejected, 0, 10));

        Assert.False(detail.Decision!.Approved);
        Assert.Null(detail.Decision.Reason);
        Assert.Equal(submission.ReviewDecision!.DecidedAt, detail.Decision.DecidedAt);
        AssertNothingRestricted(JsonSerializer.Serialize(detail), submission);
        AssertNothingRestricted(JsonSerializer.Serialize(page), submission);
    }

    [Fact]
    public async Task EveryReadModelIsFreeOfRestrictedValuesForEveryState()
    {
        var seeded = new List<ListingSubmission>();
        foreach (var state in new[] { State.Pending, State.Approved, State.Rejected })
        {
            seeded.Add(await SeedAsync(state));
        }

        foreach (var submission in seeded)
        {
            AssertNothingRestricted(JsonSerializer.Serialize((await Read(r => r.GetForReviewAsync(submission.Id))).Detail), submission);
        }

        foreach (var status in new[] { ListingSubmissionStatus.PendingReview, ListingSubmissionStatus.Approved, ListingSubmissionStatus.Rejected })
        {
            var json = JsonSerializer.Serialize(await Read(r => r.ListForReviewAsync(null, status, 0, 10)));
            foreach (var submission in seeded)
            {
                AssertNothingRestricted(json, submission);
            }
        }
    }

    [Fact]
    public async Task ADraftAnUnknownIdAndTheEmptyIdAreNotFound()
    {
        var draft = await SeedAsync(State.Draft);

        foreach (var id in new[] { draft.Id, Guid.NewGuid(), Guid.Empty })
        {
            var lookup = await Read(r => r.GetForReviewAsync(id));

            Assert.Equal(ReviewDetailOutcome.NotFound, lookup.Outcome);
            Assert.Null(lookup.Detail);
        }
    }

    [Fact]
    public async Task TheDetailOutcomeIsAlwaysFoundOrNotFoundWhoeverSubmittedOrOwnsTheSubmission()
    {
        // The repository is never told who is reading, so every reader, including the submitter and the linked owner,
        // gets the same answer for the same submission.
        var submission = await SeedAsync(State.Pending);

        var outcomes = new List<ReviewDetailOutcome>();
        foreach (var _ in new[] { submission.Authority.ActingUserId, submission.Authority.DeclaredOwner.LinkedUserId, Guid.NewGuid() })
        {
            outcomes.Add((await Read(r => r.GetForReviewAsync(submission.Id))).Outcome);
        }

        Assert.All(outcomes, outcome => Assert.Equal(ReviewDetailOutcome.Found, outcome));
        Assert.Equal(new[] { "GetForReviewAsync", "ListForReviewAsync" },
            typeof(IReviewerListingSubmissionReadRepository).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(
            typeof(IReviewerListingSubmissionReadRepository).GetMethods().SelectMany(m => m.GetParameters()),
            parameter => parameter.Name!.Contains("user", StringComparison.OrdinalIgnoreCase)
                || parameter.Name.Contains("reviewer", StringComparison.OrdinalIgnoreCase));
    }

    // ----- AllEvidenceClean

    public static TheoryData<string, string> NotCleanMutations()
    {
        const string guid = "'\"00000000-0000-0000-0000-000000000001\"'::jsonb";
        var assessments = "submitted_assessments_snapshot";
        var evidence = "evidence_snapshot";
        string Set(string column, string path, string value) => $"{column} = jsonb_set({column}, ARRAY[{path}], {value})";

        return new TheoryData<string, string>
        {
            { "scan pending", Set(assessments, "'Payload','0','Status'", "'1'::jsonb") },
            { "scan failed", Set(assessments, "'Payload','0','Status'", "'4'::jsonb") },
            { "scan rejected", Set(assessments, "'Payload','0','Status'", "'3'::jsonb") },
            { "scan unknown", Set(assessments, "'Payload','0','Status'", "'0'::jsonb") },
            { "status as a string", Set(assessments, "'Payload','0','Status'", "'\"2\"'::jsonb") },
            { "status missing", $"{assessments} = {assessments} #- ARRAY['Payload','0','Status']" },
            { "digest differs", Set(assessments, "'Payload','0','Evidence','Sha256'", $"to_jsonb('{new string('B', 64)}'::text)") },
            { "digest differs only by case", Set(assessments, "'Payload','0','Evidence','Sha256'", $"to_jsonb('{new string('a', 64)}'::text)") },
            { "version differs", Set(assessments, "'Payload','0','Evidence','VersionId'", guid) },
            { "evidence id differs", Set(assessments, "'Payload','0','Evidence','EvidenceId'", guid) },
            { "property differs", Set(assessments, "'Payload','0','Evidence','PropertyId'", guid) },
            { "purpose differs", Set(assessments, "'Payload','0','Evidence','Purpose'", "'9'::jsonb") },
            { "assessment missing", $"{assessments} = {assessments} #- ARRAY['Payload','0']" },
            { "assessment duplicated", $"{assessments} = jsonb_set({assessments}, ARRAY['Payload'], ({assessments}->'Payload') || ({assessments}->'Payload'->0))" },
            { "unrelated extra assessment", $"{assessments} = jsonb_set({assessments}, ARRAY['Payload'], ({assessments}->'Payload') || jsonb_set({assessments}->'Payload'->0, ARRAY['Evidence','EvidenceId'], {guid}))" },
            { "assessment payload not an array", Set(assessments, "'Payload'", "to_jsonb('x'::text)") },
            { "assessment element null", Set(assessments, "'Payload','0'", "'null'::jsonb") },
            { "evidence element null", Set(evidence, "'Payload','0'", "'null'::jsonb") },
            { "evidence duplicated with duplicated assessments",
                $"{evidence} = jsonb_set({evidence}, ARRAY['Payload'], ({evidence}->'Payload') || ({evidence}->'Payload'->0)), "
                + $"{assessments} = jsonb_set({assessments}, ARRAY['Payload'], ({assessments}->'Payload') || ({assessments}->'Payload'->0))" },
        };
    }

    [Fact]
    public async Task EveryEvidenceReferenceWithAnExactCleanSubmitTimeAssessmentIsAllClean()
    {
        var single = await SeedAsync(State.Pending, minute: 1, evidence: 1);
        var several = await SeedAsync(State.Pending, minute: 2, evidence: 3);

        var page = await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10));

        Assert.True(page.Items.Single(item => item.SubmissionId == single.Id).AllEvidenceClean);
        var many = page.Items.Single(item => item.SubmissionId == several.Id);
        Assert.True(many.AllEvidenceClean);
        Assert.Equal(3, many.EvidenceCount);
    }

    [Theory]
    [MemberData(nameof(NotCleanMutations))]
    public async Task AnythingMissingMismatchedPendingFailedMalformedOrAmbiguousIsNotAllClean(string name, string assignment)
    {
        var submission = await SeedAsync(State.Pending);
        Assert.True((await Read(r => r.GetForReviewAsync(submission.Id))).Detail!.AllEvidenceClean);

        await using (var context = Host.CreateVerificationContext())
        {
            // The assignment text is a constant owned by this test class, never external input.
#pragma warning disable EF1003
            Assert.Equal(1, await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.submissions SET " + assignment + " WHERE id = {0}", submission.Id));
#pragma warning restore EF1003
        }

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;
        var item = Assert.Single((await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10))).Items);
        Assert.False(detail.AllEvidenceClean, name);
        Assert.False(item.AllEvidenceClean, name);
    }

    [Fact]
    public async Task OneBadAssessmentAmongSeveralMakesTheWholeSubmissionNotAllClean()
    {
        var submission = await SeedAsync(State.Pending, evidence: 3);
        await using (var context = Host.CreateVerificationContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.submissions SET submitted_assessments_snapshot = jsonb_set(submitted_assessments_snapshot, ARRAY['Payload','2','Status'], '1'::jsonb) WHERE id = {0}",
                submission.Id);
        }

        Assert.False((await Read(r => r.GetForReviewAsync(submission.Id))).Detail!.AllEvidenceClean);
    }

    [Fact]
    public async Task AssessmentsInADifferentOrderFromTheEvidenceStillMatchExactly()
    {
        var submission = await SeedAsync(State.Pending, evidence: 2);
        await using (var context = Host.CreateVerificationContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace.submissions SET submitted_assessments_snapshot = jsonb_set(submitted_assessments_snapshot, ARRAY['Payload'], "
                + "jsonb_build_array(submitted_assessments_snapshot->'Payload'->1, submitted_assessments_snapshot->'Payload'->0)) WHERE id = {0}",
                submission.Id);
        }

        Assert.True((await Read(r => r.GetForReviewAsync(submission.Id))).Detail!.AllEvidenceClean);
    }

    // ----- JSON payload shape safety

    // Whole-payload replacements for the submit-time assessments. The schema constrains only their FormatVersion, so
    // every one of these is storable on a reviewable row. Each must read as "not all clean" and never as an error.
    public static TheoryData<string, string> MalformedAssessmentPayloads()
    {
        const string column = "submitted_assessments_snapshot";
        string Payload(string value) => $"{column} = jsonb_set({column}, ARRAY['Payload'], {value})";

        return new TheoryData<string, string>
        {
            { "assessment payload key missing", $"{column} = {column} - 'Payload'" },
            { "assessment payload JSON null", Payload("'null'::jsonb") },
            { "assessment payload an object", Payload("jsonb_build_object()") },
            { "assessment payload a number", Payload("'7'::jsonb") },
            { "assessment payload a string", Payload("to_jsonb('x'::text)") },
            { "assessment payload an empty array", Payload("'[]'::jsonb") },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedAssessmentPayloads))]
    public async Task AMissingNullNonArrayOrEmptyAssessmentPayloadIsNotAllCleanAndNeverAnError(string name, string assignment)
    {
        var submission = await SeedAsync(State.Pending, evidence: 2);
        await TamperAsync(submission.Id, assignment);

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;
        var item = Assert.Single((await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10))).Items);

        Assert.False(detail.AllEvidenceClean, name);
        Assert.False(item.AllEvidenceClean, name);
        Assert.Equal(2, detail.EvidenceCount);
        Assert.Equal(2, item.EvidenceCount);
    }

    // Whole-payload replacements for the evidence references. ck_marketplace_submissions_evidence forbids these on a
    // reviewable row, so the test lifts that one constraint inside its own disposable per-test database first; the
    // shared template, every other test database and the real schema are untouched.
    public static TheoryData<string, string> MalformedEvidencePayloads()
    {
        const string column = "evidence_snapshot";
        string Payload(string value) => $"{column} = jsonb_set({column}, ARRAY['Payload'], {value})";

        return new TheoryData<string, string>
        {
            { "evidence payload key missing", $"{column} = {column} - 'Payload'" },
            { "evidence payload JSON null", Payload("'null'::jsonb") },
            { "evidence payload an object", Payload("jsonb_build_object()") },
            { "evidence payload a string", Payload("to_jsonb('x'::text)") },
            { "evidence payload an empty array", Payload("'[]'::jsonb") },
            { "both payloads empty arrays",
                Payload("'[]'::jsonb") + ", submitted_assessments_snapshot = jsonb_set(submitted_assessments_snapshot, ARRAY['Payload'], '[]'::jsonb)" },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedEvidencePayloads))]
    public async Task AMissingNullNonArrayOrEmptyEvidencePayloadCountsZeroIsNotAllCleanAndNeverAnError(string name, string assignment)
    {
        var submission = await SeedAsync(State.Pending, evidence: 2);
        await DropEvidenceConstraintInThisTestDatabaseAsync();
        await TamperAsync(submission.Id, assignment);

        var detail = (await Read(r => r.GetForReviewAsync(submission.Id))).Detail!;
        var item = Assert.Single((await Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10))).Items);

        Assert.False(detail.AllEvidenceClean, name);
        Assert.False(item.AllEvidenceClean, name);
        Assert.Equal(0, detail.EvidenceCount);
        Assert.Equal(0, item.EvidenceCount);
    }

    [Fact]
    public void EveryJsonArrayFunctionInTheProjectionReadsOnlyANormalizedArray()
    {
        var repository = typeof(SmartProperty.Persistence.AssemblyMarker).Assembly
            .GetType("SmartProperty.Persistence.Repositories.Marketplace.ReviewerListingSubmissionReadRepository", throwOnError: true)!;
        var sql = (string)repository.GetField("ReviewableSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var calls = System.Text.RegularExpressions.Regex.Matches(sql, @"jsonb_array_(length|elements)\(([^)]*)\)");
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Matches(@"^j\.(evidence|assessments)$", call.Groups[2].Value));
        Assert.Contains("ELSE '[]'::jsonb END AS evidence", sql, StringComparison.Ordinal);
        Assert.Contains("ELSE '[]'::jsonb END AS assessments", sql, StringComparison.Ordinal);
    }

    // ----- malformed authority scalars fail closed

    // The authority members are cast without a schema guarantee. A malformed value must fail the read, never be shown
    // with a guessed or missing value; the API turns the failure into its generic 500.
    [Theory]
    [InlineData("ValidUntil", "to_jsonb('SENTINEL-NOT-A-DATE'::text)")]
    [InlineData("ValidFrom", "to_jsonb('SENTINEL-NOT-A-DATE'::text)")]
    [InlineData("Representation", "to_jsonb('SENTINEL-NOT-A-NUMBER'::text)")]
    public async Task AMalformedAuthorityScalarFailsTheReadInsteadOfShowingAGuess(string member, string value)
    {
        var submission = await SeedAsync(State.Pending);
        await TamperAsync(submission.Id,
            $"authority_snapshot = jsonb_set(authority_snapshot, ARRAY['Payload','{member}'], {value})");

        var detail = await Assert.ThrowsAnyAsync<Exception>(() => Read(r => r.GetForReviewAsync(submission.Id)));
        var list = await Assert.ThrowsAnyAsync<Exception>(() => Read(r => r.ListForReviewAsync(null, ListingSubmissionStatus.PendingReview, 0, 10)));

        foreach (var failure in new[] { detail, list })
        {
            var postgres = FindPostgresException(failure);
            Assert.NotNull(postgres);
            Assert.StartsWith("22", postgres!.SqlState, StringComparison.Ordinal);
        }
    }

    private static Npgsql.PostgresException? FindPostgresException(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is Npgsql.PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private async Task TamperAsync(Guid submissionId, string assignment)
    {
        await using var context = Host.CreateVerificationContext();
        // The assignment text is a constant owned by this test class, never external input.
#pragma warning disable EF1003
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace.submissions SET " + assignment + " WHERE id = {0}", submissionId));
#pragma warning restore EF1003
    }

    private async Task DropEvidenceConstraintInThisTestDatabaseAsync()
    {
        await using var context = Host.CreateVerificationContext();
        var expected = new Npgsql.NpgsqlConnectionStringBuilder(Host.ConnectionString).Database;
        var actual = await context.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync();

        // Only ever this test's own throwaway clone, never the template or any shared database.
        Assert.Equal(expected, actual);
        Assert.StartsWith("sp_test_", actual, StringComparison.Ordinal);

        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE marketplace.submissions DROP CONSTRAINT ck_marketplace_submissions_evidence");
    }

    // ----- structure

    [Fact]
    public void TheProjectionAndTheSqlCarryNothingRestricted()
    {
        var assembly = typeof(SmartProperty.Persistence.AssemblyMarker).Assembly;
        var repository = assembly.GetType("SmartProperty.Persistence.Repositories.Marketplace.ReviewerListingSubmissionReadRepository", throwOnError: true)!;
        var row = assembly.GetType("SmartProperty.Persistence.Repositories.Marketplace.ReviewerListingSubmissionReadRow", throwOnError: true)!;
        var sql = (string)repository.GetField("ReviewableSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (var forbidden in new[] { "d.reason", "reviewer_snapshot", "assessment_snapshot", "LegalPartyReferenceId", "LinkedUserId", "ActingUserId", "WorkspaceId" })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.Ordinal);
        }

        Assert.Contains("s.status <> 'Draft'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            row.GetProperties().Select(property => property.Name),
            name => new[] { "Reason", "Sha", "Digest", "Legal", "User", "Workspace", "Reviewer", "Snapshot", "Assessment" }
                .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)));
    }

    // ----- helpers

    private async Task<T> Read<T>(Func<IReviewerListingSubmissionReadRepository, Task<T>> action)
    {
        using var scope = services!.CreateScope();

        return await action(scope.ServiceProvider.GetRequiredService<IReviewerListingSubmissionReadRepository>());
    }

    private static void AssertNothingRestricted(string json, ListingSubmission submission)
    {
        var restricted = new[]
        {
            Sentinel, "SENTINEL", "123456789", new string('A', 64),
            submission.Authority.ActingUserId.ToString(), submission.Authority.DeclaredOwner.LegalPartyReferenceId.ToString(),
            submission.Authority.DeclaredOwner.LinkedUserId?.ToString() ?? "no-linked-user",
            submission.Evidence[0].EvidenceId.ToString(), submission.Evidence[0].VersionId.ToString(),
        };

        foreach (var value in restricted)
        {
            Assert.DoesNotContain(value, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ListingSubmission Build(PropertyListing listing, DateTimeOffset at, string title, int evidenceCount, bool submit, ListingSubmission? predecessor = null)
    {
        var actor = Guid.NewGuid();
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor), actor,
            ListingRepresentationKind.Owner, null, listing.CreatedAt, null);
        var evidence = Enumerable.Range(0, evidenceCount)
            .Select(_ => new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64)))
            .ToArray();
        var content = new ListingSubmissionContent("Country", "Region", "City", "District", title, "Description",
            PropertyType.Unit, listing.Purpose, 100m, "USD", listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, evidence, at, predecessor);

        if (submit)
        {
            submission.Submit(actor, null, at, MarketplaceGraph.Assessments(submission, at), true, null);
        }

        return submission;
    }

    private async Task<ListingSubmission> SeedAsync(
        State state,
        ListingPurpose purpose = ListingPurpose.Sale,
        int minute = 1,
        string title = "Title",
        int evidence = 1,
        string reason = "Not enough evidence.")
    {
        await using var context = Host.CreateVerificationContext();
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), T0);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, purpose, T0);
        context.Add(property);
        context.Add(listing);
        await context.SaveChangesAsync();

        var at = T0.AddMinutes(minute);
        var submission = Build(listing, at, title, evidence, submit: state != State.Draft);
        context.Add(submission);
        await context.SaveChangesAsync();

        var decidedAt = at.AddSeconds(30);

        if (state == State.Approved)
        {
            submission.ApproveAndPublish(Guid.NewGuid(), listing, null, MarketplaceGraph.Reviewer(submission), decidedAt, true,
                MarketplaceGraph.Assessments(submission, decidedAt), true, null);
            await context.SaveChangesAsync();
        }
        else if (state == State.Rejected)
        {
            submission.Reject(Guid.NewGuid(), MarketplaceGraph.Reviewer(submission), reason, decidedAt);
            await context.SaveChangesAsync();
        }

        return submission;
    }

    private async Task<(ListingSubmission First, ListingSubmission Second)> SeedSupersededAsync()
    {
        await using var context = Host.CreateVerificationContext();
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), T0);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Sale, T0);
        context.Add(property);
        context.Add(listing);
        await context.SaveChangesAsync();

        var firstAt = T0.AddMinutes(1);
        var first = Build(listing, firstAt, "First", 1, submit: true);
        context.Add(first);
        await context.SaveChangesAsync();
        first.ApproveAndPublish(Guid.NewGuid(), listing, null, MarketplaceGraph.Reviewer(first), firstAt.AddSeconds(10), true,
            MarketplaceGraph.Assessments(first, firstAt.AddSeconds(10)), true, null);
        await context.SaveChangesAsync();

        var secondAt = T0.AddMinutes(2);
        var second = Build(listing, secondAt, "Second", 1, submit: true, predecessor: first);
        context.Add(second);
        await context.SaveChangesAsync();
        second.ApproveAndPublish(Guid.NewGuid(), listing, first, MarketplaceGraph.Reviewer(second), secondAt.AddSeconds(10), true,
            MarketplaceGraph.Assessments(second, secondAt.AddSeconds(10)), true, null);
        await context.SaveChangesAsync();

        return (first, second);
    }
}
