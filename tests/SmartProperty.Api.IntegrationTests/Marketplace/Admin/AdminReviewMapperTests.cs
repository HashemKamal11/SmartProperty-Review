using System.Text.Json;
using SmartProperty.Api.Contracts.Marketplace.Admin;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// The mapping from the Application review read models to the Platform Admin response contracts: exact field
/// mapping, the rent period rules, the decision rules, and failing closed on a read model that contradicts an
/// invariant the Domain enforces.
/// </summary>
/// <remarks>
/// No host, no database, no endpoint: the mapper is a pure function.
/// </remarks>
public sealed class AdminReviewMapperTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SubmissionId = Guid.Parse("b1d7a3c2-5e44-4a0e-8f21-6c9d2e7a1b10");
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly Guid PropertyId = Guid.Parse("c7f2d9e4-1a6b-4c3d-8e5f-0b9a8d7c6e50");
    private static readonly Guid PredecessorId = Guid.Parse("d3e8b1a7-9c2f-4d6e-a5b4-7f1e0c2d3a90");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidFrom = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidUntil = new(2027, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASaleQueueItemMapsEveryFieldAndHasNoRentPeriod()
    {
        var response = AdminReviewMapper.ToQueueItemResponse(SaleItem());

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
        Assert.Equal("Individual", response.OwnerKind);
        Assert.Equal(Submitted, response.SubmittedAt);
        Assert.Equal(2, response.EvidenceCount);
        Assert.True(response.AllEvidenceClean);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly, "Monthly")]
    [InlineData(RentPeriod.Yearly, "Yearly")]
    public void ARentQueueItemCarriesItsRentPeriod(RentPeriod period, string expected)
    {
        var response = AdminReviewMapper.ToQueueItemResponse(RentItem(period));

        Assert.Equal("Rent", response.Purpose);
        Assert.Equal(expected, response.Price.RentPeriod);
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview, "PendingReview")]
    [InlineData(ListingSubmissionStatus.Approved, "Approved")]
    [InlineData(ListingSubmissionStatus.Rejected, "Rejected")]
    [InlineData(ListingSubmissionStatus.Superseded, "Superseded")]
    public void EveryReviewableStatusIsItsName(ListingSubmissionStatus status, string expected)
    {
        Assert.Equal(expected, AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Status = status }).Status);
    }

    [Theory]
    [InlineData(ListingRepresentationKind.Owner, "Owner")]
    [InlineData(ListingRepresentationKind.IndividualRepresentative, "IndividualRepresentative")]
    [InlineData(ListingRepresentationKind.WorkspaceRepresentative, "WorkspaceRepresentative")]
    public void EveryRepresentationIsItsName(ListingRepresentationKind representation, string expected)
    {
        var item = SaleItem() with { Representation = representation };

        Assert.Equal(expected, AdminReviewMapper.ToQueueItemResponse(item).Representation);
    }

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Building, "Building")]
    [InlineData(PropertyType.Unit, "Unit")]
    public void EveryPropertyTypeIsItsName(PropertyType type, string expected)
    {
        Assert.Equal(expected, AdminReviewMapper.ToQueueItemResponse(SaleItem() with { PropertyType = type }).PropertyType);
    }

    [Fact]
    public void AnOrganizationRepresentedByAnotherPartyMapsAsAnOrganization()
    {
        var item = SaleItem() with
        {
            Representation = ListingRepresentationKind.WorkspaceRepresentative,
            OwnerKind = ListingOwnerKind.Organization,
        };

        Assert.Equal("Organization", AdminReviewMapper.ToQueueItemResponse(item).OwnerKind);
    }

    [Fact]
    public void ThePendingDetailMapsEveryFieldAndOmitsTheOptionalOnes()
    {
        var response = AdminReviewMapper.ToDetailResponse(PendingDetail());

        Assert.Equal(SubmissionId, response.SubmissionId);
        Assert.Equal(ListingId, response.ListingId);
        Assert.Equal(PropertyId, response.PropertyId);
        Assert.Null(response.PredecessorSubmissionId);
        Assert.Equal("Sale", response.Purpose);
        Assert.Equal(1, response.Version);
        Assert.Equal("PendingReview", response.Status);
        Assert.Equal("Two bedroom apartment", response.Title);
        Assert.Equal("Close to the centre.", response.Description);
        Assert.Equal(1250000m, response.Price.Amount);
        Assert.Equal(ValidFrom, response.AuthorityValidFrom);
        Assert.Null(response.AuthorityValidUntil);
        Assert.Equal(Created, response.CreatedAt);
        Assert.Equal(Submitted, response.SubmittedAt);
        Assert.Equal(Submitted, response.UpdatedAt);
        Assert.Equal(2, response.EvidenceCount);
        Assert.True(response.AllEvidenceClean);
        Assert.False(response.IsCurrentlyPublished);
        Assert.Null(response.Decision);

        var json = JsonSerializer.Serialize(response, WebOptions);
        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("predecessorSubmissionId", keys);
        Assert.DoesNotContain("authorityValidUntil", keys);
        Assert.DoesNotContain("decision", keys);
        Assert.DoesNotContain("rentPeriod", document.RootElement.GetProperty("price").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void ARentDetailWithAWindowAndAPredecessorMapsThem()
    {
        var detail = PendingDetail() with
        {
            Purpose = ListingPurpose.Rent,
            Price = new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            Version = 2,
            PredecessorSubmissionId = PredecessorId,
            AuthorityValidUntil = ValidUntil,
        };

        var response = AdminReviewMapper.ToDetailResponse(detail);

        Assert.Equal("Rent", response.Purpose);
        Assert.Equal("Monthly", response.Price.RentPeriod);
        Assert.Equal(2, response.Version);
        Assert.Equal(PredecessorId, response.PredecessorSubmissionId);
        Assert.Equal(ValidUntil, response.AuthorityValidUntil);
    }

    [Fact]
    public void AnApprovedDetailCarriesAnApprovalWithNoReasonWhateverIsStored()
    {
        var detail = ApprovedDetail(new ReviewDecisionSummary(Approved: true, "Internal approval note.", Decided));

        var response = AdminReviewMapper.ToDetailResponse(detail);

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
            Decision = new ReviewDecisionSummary(Approved: false, "The deed does not cover this parcel.", Decided),
        };

        var response = AdminReviewMapper.ToDetailResponse(detail);

        Assert.Equal("Rejected", response.Status);
        Assert.False(response.Decision!.Approved);
        Assert.Equal("The deed does not cover this parcel.", response.Decision.Reason);
    }

    [Fact]
    public void ASupersededDetailKeepsItsEarlierApproval()
    {
        var detail = ApprovedDetail(new ReviewDecisionSummary(Approved: true, null, Decided)) with
        {
            Status = ListingSubmissionStatus.Superseded,
            UpdatedAt = Decided.AddDays(30),
            IsCurrentlyPublished = false,
        };

        var response = AdminReviewMapper.ToDetailResponse(detail);

        Assert.Equal("Superseded", response.Status);
        Assert.True(response.Decision!.Approved);
    }

    [Fact]
    public void ACurrentlyPublishedApprovedDetailSaysSo()
    {
        var detail = ApprovedDetail(new ReviewDecisionSummary(Approved: true, null, Decided)) with { IsCurrentlyPublished = true };

        Assert.True(AdminReviewMapper.ToDetailResponse(detail).IsCurrentlyPublished);
    }

    [Fact]
    public void ANullModelIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => AdminReviewMapper.ToQueueItemResponse(null!));
        Assert.Throws<ArgumentNullException>(() => AdminReviewMapper.ToDetailResponse(null!));
    }

    // ----- fail closed: queue item

    [Fact]
    public void AQueueItemWithAnEmptyIdIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { SubmissionId = Guid.Empty }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { ListingId = Guid.Empty }));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.Draft)]
    [InlineData((ListingSubmissionStatus)99)]
    public void AQueueItemThatIsNotReviewableIsRefused(ListingSubmissionStatus status)
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Status = status }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AQueueItemWithAVersionBelowOneIsRefused(int version)
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Version = version }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void AQueueItemWithoutEvidenceIsRefused(int count)
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { EvidenceCount = count }));
    }

    [Fact]
    public void AQueueItemWithUndefinedEnumerationsIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Purpose = (ListingPurpose)99 }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { PropertyType = (PropertyType)99 }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Representation = (ListingRepresentationKind)99 }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { OwnerKind = (ListingOwnerKind)99 }));
    }

    [Fact]
    public void AnOrganizationActingAsItsOwnOwnerIsRefused()
    {
        var item = SaleItem() with { Representation = ListingRepresentationKind.Owner, OwnerKind = ListingOwnerKind.Organization };

        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(item));
    }

    [Fact]
    public void AQueueItemWithoutASubmissionTimeIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { SubmittedAt = default }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTitleIsRefused(string title)
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Title = title }));
    }

    [Fact]
    public void ABlankCurrencyOrLocationPartIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Price = new PublicListingPrice(10m, " ", null) }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Location = new PublicListingLocation("", "R", "C", "D") }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Location = new PublicListingLocation("SA", " ", "C", "D") }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Location = new PublicListingLocation("SA", "R", "", "D") }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(SaleItem() with { Location = new PublicListingLocation("SA", "R", "C", " ") }));
    }

    [Fact]
    public void ARentPeriodOnASaleOrNoneOnARentalOrAnUndefinedOneIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(
            SaleItem() with { Price = new PublicListingPrice(10m, "SAR", RentPeriod.Monthly) }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(
            RentItem(RentPeriod.Monthly) with { Price = new PublicListingPrice(10m, "SAR", null) }));
        AssertInvalid(() => AdminReviewMapper.ToQueueItemResponse(
            RentItem(RentPeriod.Monthly) with { Price = new PublicListingPrice(10m, "SAR", (RentPeriod)99) }));
    }

    // ----- fail closed: detail

    [Fact]
    public void ADetailWithAnEmptyIdIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { SubmissionId = Guid.Empty }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { ListingId = Guid.Empty }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { PropertyId = Guid.Empty }));
    }

    [Fact]
    public void ADraftOrUndefinedStatusDetailIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { Status = ListingSubmissionStatus.Draft }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { Status = (ListingSubmissionStatus)99 }));
    }

    [Fact]
    public void AFirstVersionWithAPredecessorIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { PredecessorSubmissionId = PredecessorId }));
    }

    [Fact]
    public void ALaterVersionWithoutAValidPredecessorIsRefused()
    {
        var later = PendingDetail() with { Version = 2 };

        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(later with { PredecessorSubmissionId = null }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(later with { PredecessorSubmissionId = Guid.Empty }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(later with { PredecessorSubmissionId = SubmissionId }));
    }

    [Fact]
    public void AnAuthorityWindowThatDoesNotEndAfterItStartsIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { AuthorityValidUntil = ValidFrom }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { AuthorityValidUntil = ValidFrom.AddDays(-1) }));
    }

    [Fact]
    public void MissingTimestampsAreRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { CreatedAt = default }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { SubmittedAt = default }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { UpdatedAt = default }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { AuthorityValidFrom = default }));
    }

    [Fact]
    public void TimestampsOutOfOrderAreRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { SubmittedAt = Created.AddDays(-1) }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { UpdatedAt = Submitted.AddDays(-1) }));
    }

    [Fact]
    public void ADetailWithoutEvidenceOrABlankDescriptionIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { EvidenceCount = 0 }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { Description = " " }));
    }

    [Fact]
    public void ADetailWithAnOrganizationActingAsOwnerOrAnUndefinedEnumIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            PendingDetail() with { Representation = ListingRepresentationKind.Owner, OwnerKind = ListingOwnerKind.Organization }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { Purpose = (ListingPurpose)99 }));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { PropertyType = (PropertyType)99 }));
    }

    [Fact]
    public void ARentalDetailWithoutARentPeriodIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(PendingDetail() with { Purpose = ListingPurpose.Rent }));
    }

    [Fact]
    public void APendingDetailWithADecisionIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            PendingDetail() with { Decision = new ReviewDecisionSummary(true, null, Decided) }));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.Approved)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void AReviewedDetailWithoutADecisionIsRefused(ListingSubmissionStatus status)
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            PendingDetail() with { Status = status, UpdatedAt = Decided, Decision = null }));
    }

    [Fact]
    public void ARejectionOnAnApprovedSubmissionOrAnApprovalOnARejectedOneIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            ApprovedDetail(new ReviewDecisionSummary(Approved: false, "No.", Decided))));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            PendingDetail() with
            {
                Status = ListingSubmissionStatus.Rejected,
                UpdatedAt = Decided,
                Decision = new ReviewDecisionSummary(Approved: true, null, Decided),
            }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ARejectionWithoutAReasonIsRefused(string? reason)
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            PendingDetail() with
            {
                Status = ListingSubmissionStatus.Rejected,
                UpdatedAt = Decided,
                Decision = new ReviewDecisionSummary(Approved: false, reason, Decided),
            }));
    }

    [Fact]
    public void ADecisionOutsideTheSubmissionLifetimeIsRefused()
    {
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            ApprovedDetail(new ReviewDecisionSummary(true, null, Submitted.AddDays(-1)))));
        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(
            ApprovedDetail(new ReviewDecisionSummary(true, null, Decided.AddDays(1)))));
    }

    [Theory]
    [InlineData(ListingSubmissionStatus.PendingReview)]
    [InlineData(ListingSubmissionStatus.Rejected)]
    [InlineData(ListingSubmissionStatus.Superseded)]
    public void OnlyAnApprovedSubmissionCanBeCurrentlyPublished(ListingSubmissionStatus status)
    {
        var detail = status switch
        {
            ListingSubmissionStatus.PendingReview => PendingDetail(),
            ListingSubmissionStatus.Rejected => PendingDetail() with
            {
                Status = status,
                UpdatedAt = Decided,
                Decision = new ReviewDecisionSummary(false, "No.", Decided),
            },
            _ => ApprovedDetail(new ReviewDecisionSummary(true, null, Decided)) with { Status = status },
        };

        AssertInvalid(() => AdminReviewMapper.ToDetailResponse(detail with { IsCurrentlyPublished = true }));
    }

    [Fact]
    public void ARefusalNamesTheFaultAndNothingFromTheRecord()
    {
        var item = SaleItem() with { Title = "SENTINEL-TITLE", Version = 0 };

        var exception = Assert.Throws<InvalidOperationException>(() => AdminReviewMapper.ToQueueItemResponse(item));

        Assert.DoesNotContain("SENTINEL", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SubmissionId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ListingId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("A review read model is invalid:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapperDrawsNoConclusionAboutIndependence()
    {
        // The same record maps the same way whoever the reviewer might be: the mapper takes no reviewer input and
        // the response has no member that could carry an independence verdict.
        Assert.Equal(
            AdminReviewMapper.ToQueueItemResponse(SaleItem()),
            AdminReviewMapper.ToQueueItemResponse(SaleItem()));
        Assert.DoesNotContain(
            typeof(ReviewQueueItemResponse).GetProperties().Select(property => property.Name),
            name => name.Contains("indep", StringComparison.OrdinalIgnoreCase)
                || name.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertInvalid(Func<object> map)
    {
        Assert.Throws<InvalidOperationException>(map);
    }

    private static ReviewQueueItem SaleItem()
    {
        return new ReviewQueueItem(
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
            ListingOwnerKind.Individual,
            Submitted,
            2,
            true);
    }

    private static ReviewQueueItem RentItem(RentPeriod period)
    {
        return SaleItem() with
        {
            Purpose = ListingPurpose.Rent,
            Price = new PublicListingPrice(5200m, "SAR", period),
        };
    }

    private static ReviewSubmissionDetail PendingDetail()
    {
        return new ReviewSubmissionDetail(
            SubmissionId,
            ListingId,
            PropertyId,
            null,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.PendingReview,
            "Two bedroom apartment",
            "Close to the centre.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            ListingOwnerKind.Individual,
            ValidFrom,
            null,
            Created,
            Submitted,
            Submitted,
            2,
            true,
            false,
            null);
    }

    private static ReviewSubmissionDetail ApprovedDetail(ReviewDecisionSummary decision)
    {
        return PendingDetail() with
        {
            Status = ListingSubmissionStatus.Approved,
            UpdatedAt = Decided,
            Decision = decision,
        };
    }
}
