using System.Text.Json;
using SmartProperty.Api.Contracts.Marketplace.Submitter;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// The mapping from the Application submitter read models to the submitter response contracts: exact field mapping,
/// every status including Draft, the rent period rules, the decision and reason rules, paging, and failing closed on
/// a read model that contradicts an invariant the Domain enforces.
/// </summary>
/// <remarks>
/// No host, no database, no endpoint: the mapper is a pure function.
/// </remarks>
public sealed class SubmitterSubmissionMapperTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SubmissionId = Guid.Parse("b1d7a3c2-5e44-4a0e-8f21-6c9d2e7a1b10");
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    // ----- summary

    [Fact]
    public void ASaleSummaryMapsEveryFieldAndHasNoRentPeriod()
    {
        var response = SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary());

        Assert.Equal(SubmissionId, response.SubmissionId);
        Assert.Equal(ListingId, response.ListingId);
        Assert.Equal("Sale", response.Purpose);
        Assert.Equal(1, response.Version);
        Assert.Equal("PendingReview", response.Status);
        Assert.Equal("Two bedroom apartment", response.Title);
        Assert.Equal(1250000m, response.Price.Amount);
        Assert.Equal("SAR", response.Price.Currency);
        Assert.Null(response.Price.RentPeriod);
        Assert.Equal("Unit", response.PropertyType);
        Assert.Equal("SA", response.Location.CountryCode);
        Assert.Equal("Riyadh", response.Location.City);
        Assert.Equal("Riyadh Province", response.Location.Region);
        Assert.Equal("Al Olaya", response.Location.District);
        Assert.Equal("Owner", response.Representation);
        Assert.Equal(Created, response.CreatedAt);
        Assert.Equal(Submitted, response.SubmittedAt);
        Assert.Equal(Submitted, response.UpdatedAt);
        Assert.False(response.IsCurrentlyPublished);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly, "Monthly")]
    [InlineData(RentPeriod.Yearly, "Yearly")]
    public void ARentSummaryCarriesItsRentPeriod(RentPeriod period, string expected)
    {
        var summary = PendingSummary() with
        {
            Purpose = ListingPurpose.Rent,
            Price = new PublicListingPrice(5200m, "SAR", period),
        };

        var response = SubmitterSubmissionMapper.ToSummaryResponse(summary);

        Assert.Equal("Rent", response.Purpose);
        Assert.Equal(expected, response.Price.RentPeriod);
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview, "PendingReview")]
    [InlineData(ListingSubmissionStatus.Approved, "Approved")]
    [InlineData(ListingSubmissionStatus.Rejected, "Rejected")]
    [InlineData(ListingSubmissionStatus.Superseded, "Superseded")]
    public void EverySubmittedStatusIsItsNameAndKeepsItsSubmissionTime(ListingSubmissionStatus status, string expected)
    {
        var response = SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Status = status });

        Assert.Equal(expected, response.Status);
        Assert.Equal(Submitted, response.SubmittedAt);
    }

    [Fact]
    public void ADraftSummaryIsValidHasNoSubmissionTimeAndOmitsIt()
    {
        var response = SubmitterSubmissionMapper.ToSummaryResponse(DraftSummary());

        Assert.Equal("Draft", response.Status);
        Assert.Null(response.SubmittedAt);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebOptions));
        Assert.DoesNotContain("submittedAt", document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData(ListingRepresentationKind.Owner, "Owner")]
    [InlineData(ListingRepresentationKind.IndividualRepresentative, "IndividualRepresentative")]
    [InlineData(ListingRepresentationKind.WorkspaceRepresentative, "WorkspaceRepresentative")]
    public void EveryRepresentationIsItsName(ListingRepresentationKind representation, string expected)
    {
        var response = SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Representation = representation });

        Assert.Equal(expected, response.Representation);
    }

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Building, "Building")]
    [InlineData(PropertyType.Unit, "Unit")]
    public void EveryPropertyTypeIsItsName(PropertyType type, string expected)
    {
        Assert.Equal(
            expected,
            SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { PropertyType = type }).PropertyType);
    }

    [Fact]
    public void ACurrentlyPublishedApprovedSummarySaysSo()
    {
        var summary = PendingSummary() with { Status = ListingSubmissionStatus.Approved, UpdatedAt = Decided, IsCurrentlyPublished = true };

        Assert.True(SubmitterSubmissionMapper.ToSummaryResponse(summary).IsCurrentlyPublished);
    }

    // ----- detail

    [Fact]
    public void APendingDetailMapsEveryFieldAndOmitsTheDecision()
    {
        var response = SubmitterSubmissionMapper.ToDetailResponse(PendingDetail());

        Assert.Equal(SubmissionId, response.SubmissionId);
        Assert.Equal(ListingId, response.ListingId);
        Assert.Equal("Close to the centre.", response.Description);
        Assert.Equal("PendingReview", response.Status);
        Assert.Equal(Submitted, response.SubmittedAt);
        Assert.Null(response.Decision);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebOptions));
        Assert.DoesNotContain("decision", document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void ADraftDetailHasNeitherASubmissionTimeNorADecision()
    {
        var response = SubmitterSubmissionMapper.ToDetailResponse(DraftDetail());

        Assert.Equal("Draft", response.Status);
        Assert.Null(response.SubmittedAt);
        Assert.Null(response.Decision);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WebOptions));
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("submittedAt", keys);
        Assert.DoesNotContain("decision", keys);
    }

    [Fact]
    public void AnApprovedDetailCarriesAnApprovalWithNoReasonWhateverIsStored()
    {
        var detail = ApprovedDetail(new SubmitterSubmissionDecision(Approved: true, "Internal approval note.", Decided));

        var response = SubmitterSubmissionMapper.ToDetailResponse(detail);

        Assert.Equal("Approved", response.Status);
        Assert.True(response.Decision!.Approved);
        Assert.Null(response.Decision.Reason);
        Assert.Equal(Decided, response.Decision.DecidedAt);
        Assert.DoesNotContain("Internal approval note", JsonSerializer.Serialize(response, WebOptions), StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedDetailCarriesTheRejectionAndItsReason()
    {
        var detail = PendingDetail() with
        {
            Status = ListingSubmissionStatus.Rejected,
            UpdatedAt = Decided,
            Decision = new SubmitterSubmissionDecision(Approved: false, "The deed does not cover this parcel.", Decided),
        };

        var response = SubmitterSubmissionMapper.ToDetailResponse(detail);

        Assert.Equal("Rejected", response.Status);
        Assert.False(response.Decision!.Approved);
        Assert.Equal("The deed does not cover this parcel.", response.Decision.Reason);
    }

    [Fact]
    public void ASupersededDetailKeepsItsEarlierApproval()
    {
        var detail = ApprovedDetail(new SubmitterSubmissionDecision(Approved: true, null, Decided)) with
        {
            Status = ListingSubmissionStatus.Superseded,
            UpdatedAt = Decided.AddDays(30),
        };

        var response = SubmitterSubmissionMapper.ToDetailResponse(detail);

        Assert.Equal("Superseded", response.Status);
        Assert.True(response.Decision!.Approved);
        Assert.Null(response.Decision.Reason);
    }

    [Fact]
    public void ARentDetailCarriesItsRentPeriod()
    {
        var detail = PendingDetail() with
        {
            Purpose = ListingPurpose.Rent,
            Price = new PublicListingPrice(5200m, "SAR", RentPeriod.Yearly),
        };

        Assert.Equal("Yearly", SubmitterSubmissionMapper.ToDetailResponse(detail).Price.RentPeriod);
    }

    // ----- paging

    [Fact]
    public void APageMapsItsItemsAndItsMetadata()
    {
        var items = new[] { PendingSummary(), DraftSummary() with { SubmissionId = Guid.NewGuid() } };
        var page = PagedList<SubmitterSubmissionSummary>.Create(items, pageNumber: 2, pageSize: 2, totalCount: 5);

        var response = SubmitterSubmissionMapper.ToPagedResponse(page);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal("PendingReview", response.Items[0].Status);
        Assert.Equal("Draft", response.Items[1].Status);
        Assert.Equal(2, response.Page);
        Assert.Equal(2, response.PageSize);
        Assert.Equal(5, response.TotalCount);
        Assert.Equal(3, response.TotalPages);
        Assert.True(response.HasNext);
        Assert.True(response.HasPrevious);
    }

    [Fact]
    public void AnEmptyPageMapsToAnEmptyResponse()
    {
        var page = PagedList<SubmitterSubmissionSummary>.Create([], pageNumber: 1, pageSize: 10, totalCount: 0);

        var response = SubmitterSubmissionMapper.ToPagedResponse(page);

        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.False(response.HasNext);
        Assert.False(response.HasPrevious);
    }

    [Fact]
    public void ACorruptItemFailsTheWholePageClosed()
    {
        var page = PagedList<SubmitterSubmissionSummary>.Create(
            [PendingSummary(), PendingSummary() with { SubmissionId = Guid.Empty }],
            pageNumber: 1,
            pageSize: 10,
            totalCount: 2);

        AssertInvalid(() => SubmitterSubmissionMapper.ToPagedResponse(page));
    }

    [Fact]
    public void ANullModelIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => SubmitterSubmissionMapper.ToSummaryResponse(null!));
        Assert.Throws<ArgumentNullException>(() => SubmitterSubmissionMapper.ToDetailResponse(null!));
        Assert.Throws<ArgumentNullException>(() => SubmitterSubmissionMapper.ToPagedResponse(null!));
    }

    // ----- fail closed: shared fields

    [Fact]
    public void AnEmptyIdIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { SubmissionId = Guid.Empty }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { ListingId = Guid.Empty }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { SubmissionId = Guid.Empty }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { ListingId = Guid.Empty }));
    }

    [Fact]
    public void AnUndefinedStatusIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Status = (ListingSubmissionStatus)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Status = (ListingSubmissionStatus)99 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AVersionBelowOneIsRefused(int version)
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Version = version }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Version = version }));
    }

    [Fact]
    public void UndefinedEnumerationsAreRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Purpose = (ListingPurpose)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { PropertyType = (PropertyType)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Representation = (ListingRepresentationKind)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Purpose = (ListingPurpose)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { PropertyType = (PropertyType)99 }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Representation = (ListingRepresentationKind)99 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTitleOrDescriptionIsRefused(string text)
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Title = text }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Title = text }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { Description = text }));
    }

    [Fact]
    public void ABlankCurrencyOrLocationPartIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Price = new PublicListingPrice(10m, " ", null) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Location = new PublicListingLocation("", "R", "C", "D") }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Location = new PublicListingLocation("SA", " ", "C", "D") }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Location = new PublicListingLocation("SA", "R", "", "D") }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Location = new PublicListingLocation("SA", "R", "C", " ") }));
    }

    [Fact]
    public void ARentPeriodOnASaleOrNoneOnARentalOrAnUndefinedOneIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(
            PendingSummary() with { Price = new PublicListingPrice(10m, "SAR", RentPeriod.Monthly) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(
            PendingSummary() with { Purpose = ListingPurpose.Rent, Price = new PublicListingPrice(10m, "SAR", null) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(
            PendingSummary() with { Purpose = ListingPurpose.Rent, Price = new PublicListingPrice(10m, "SAR", (RentPeriod)99) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            PendingDetail() with { Purpose = ListingPurpose.Rent }));
    }

    [Fact]
    public void AMissingPriceOrLocationIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Price = null! }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Location = null! }));
    }

    // ----- fail closed: timestamps

    [Fact]
    public void MissingTimestampsAreRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { CreatedAt = default }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { UpdatedAt = default }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { SubmittedAt = default(DateTimeOffset) }));
    }

    [Fact]
    public void TimestampsOutOfOrderAreRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { UpdatedAt = Created.AddDays(-1) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { SubmittedAt = Created.AddDays(-1) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { SubmittedAt = Submitted.AddDays(1) }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { UpdatedAt = Created.AddDays(-1) }));
    }

    [Fact]
    public void ADraftWithASubmissionTimeIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(DraftSummary() with { SubmittedAt = Submitted }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(DraftDetail() with { SubmittedAt = Submitted }));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void ASubmittedStatusWithoutASubmissionTimeIsRefused(ListingSubmissionStatus status)
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary() with { Status = status, SubmittedAt = null }));
    }

    // ----- fail closed: publication and decisions

    [Theory]
    [InlineData(ListingSubmissionStatus.Draft)]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void OnlyAnApprovedSubmissionCanBeCurrentlyPublished(ListingSubmissionStatus status)
    {
        var summary = status == ListingSubmissionStatus.Draft
            ? DraftSummary() with { IsCurrentlyPublished = true }
            : PendingSummary() with { Status = status, IsCurrentlyPublished = true };

        AssertInvalid(() => SubmitterSubmissionMapper.ToSummaryResponse(summary));
    }

    [Fact]
    public void ADecisionOnADraftOrAPendingSubmissionIsRefused()
    {
        var decision = new SubmitterSubmissionDecision(true, null, Decided);

        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(DraftDetail() with { Decision = decision }));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(PendingDetail() with { UpdatedAt = Decided, Decision = decision }));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void AReviewedDetailWithoutADecisionIsRefused(ListingSubmissionStatus status)
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            PendingDetail() with { Status = status, UpdatedAt = Decided, Decision = null }));
    }

    [Fact]
    public void ARejectionOnAnApprovedSubmissionOrAnApprovalOnARejectedOneIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            ApprovedDetail(new SubmitterSubmissionDecision(Approved: false, "No.", Decided))));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            PendingDetail() with
            {
                Status = ListingSubmissionStatus.Rejected,
                UpdatedAt = Decided,
                Decision = new SubmitterSubmissionDecision(Approved: true, null, Decided),
            }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ARejectionWithoutAReasonIsRefused(string? reason)
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            PendingDetail() with
            {
                Status = ListingSubmissionStatus.Rejected,
                UpdatedAt = Decided,
                Decision = new SubmitterSubmissionDecision(Approved: false, reason, Decided),
            }));
    }

    [Fact]
    public void ADecisionOutsideTheLifetimeOfTheSubmissionIsRefused()
    {
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            ApprovedDetail(new SubmitterSubmissionDecision(true, null, Submitted.AddDays(-1)))));
        AssertInvalid(() => SubmitterSubmissionMapper.ToDetailResponse(
            ApprovedDetail(new SubmitterSubmissionDecision(true, null, Decided.AddDays(1)))));
    }

    // ----- no disclosure, no conclusions

    [Fact]
    public void ARefusalNamesTheFaultAndNothingFromTheRecord()
    {
        var summary = PendingSummary() with { Title = "SENTINEL-TITLE", Version = 0 };

        var exception = Assert.Throws<InvalidOperationException>(() => SubmitterSubmissionMapper.ToSummaryResponse(summary));

        Assert.DoesNotContain("SENTINEL", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SubmissionId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ListingId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("A submitter read model is invalid:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapperTakesNoUserAndDrawsNoOwnershipOrAuthorizationConclusion()
    {
        // Every mapper entry point takes a read model (or a page of them) and nothing else, so it cannot learn whose
        // submission it is mapping; the response has no member that could carry such a verdict.
        var methods = typeof(SubmitterSubmissionMapper)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.All(methods, method => Assert.Single(method.GetParameters()));
        Assert.DoesNotContain(
            typeof(SubmitterSubmissionDetailResponse).GetProperties().Select(property => property.Name),
            name => name.Contains("owner", StringComparison.OrdinalIgnoreCase)
                || name.Contains("authoriz", StringComparison.OrdinalIgnoreCase)
                || name.Contains("account", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertInvalid(Func<object> map)
    {
        Assert.Throws<InvalidOperationException>(map);
    }

    private static SubmitterSubmissionSummary PendingSummary()
    {
        return new SubmitterSubmissionSummary(
            SubmissionId,
            ListingId,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.PendingReview,
            "Two bedroom apartment",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            Created,
            Submitted,
            Submitted,
            false);
    }

    private static SubmitterSubmissionSummary DraftSummary()
    {
        return PendingSummary() with
        {
            Status = ListingSubmissionStatus.Draft,
            SubmittedAt = null,
            UpdatedAt = Created.AddHours(1),
        };
    }

    private static SubmitterSubmissionDetail PendingDetail()
    {
        return new SubmitterSubmissionDetail(
            SubmissionId,
            ListingId,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.PendingReview,
            "Two bedroom apartment",
            "Close to the centre.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            Created,
            Submitted,
            Submitted,
            false,
            null);
    }

    private static SubmitterSubmissionDetail DraftDetail()
    {
        return PendingDetail() with
        {
            Status = ListingSubmissionStatus.Draft,
            SubmittedAt = null,
            UpdatedAt = Created.AddHours(1),
        };
    }

    private static SubmitterSubmissionDetail ApprovedDetail(SubmitterSubmissionDecision decision)
    {
        return PendingDetail() with
        {
            Status = ListingSubmissionStatus.Approved,
            UpdatedAt = Decided,
            Decision = decision,
        };
    }
}
