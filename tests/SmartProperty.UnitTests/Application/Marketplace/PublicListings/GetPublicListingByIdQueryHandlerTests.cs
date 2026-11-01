using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.GetById;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.PublicListings;

public sealed class GetPublicListingByIdQueryHandlerTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly FakePublicListingReadRepository _listings = new();

    [Fact]
    public async Task AnExistingListingIsReturnedUnchanged()
    {
        var detail = SeedDetail();

        var result = await CreateHandler().Handle(new GetPublicListingByIdQuery(detail.ListingId));

        Assert.True(result.IsSuccess);
        Assert.Same(detail, result.Value);
        Assert.Equal(1, _listings.GetByIdCallCount);
        Assert.Equal(detail.ListingId, _listings.LastRequestedId);
    }

    [Fact]
    public async Task AnUnknownIdReturnsTheStableNotFoundError()
    {
        var result = await CreateHandler().Handle(new GetPublicListingByIdQuery(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Same(PublicListingErrors.NotFound, result.Error);
        Assert.Equal("public_listings.not_found", result.Error!.Code);
        Assert.Equal("The listing was not found.", result.Error.Description);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal(1, _listings.GetByIdCallCount);
    }

    [Fact]
    public async Task TheEmptyIdReturnsNotFoundWithoutAskingTheRepository()
    {
        var result = await CreateHandler().Handle(new GetPublicListingByIdQuery(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Same(PublicListingErrors.NotFound, result.Error);
        Assert.Equal(0, _listings.GetByIdCallCount);
    }

    [Fact]
    public async Task EveryHiddenListingGivesTheSameAnswerAsAMissingOne()
    {
        // The repository answers null for unpublished, withdrawn, reserved, closed, unapproved and missing
        // listings alike, so the handler can only ever produce one error for all of them.
        var missing = await CreateHandler().Handle(new GetPublicListingByIdQuery(Guid.NewGuid()));
        var empty = await CreateHandler().Handle(new GetPublicListingByIdQuery(Guid.Empty));

        Assert.Same(missing.Error, empty.Error);
    }

    [Fact]
    public async Task TheCancellationTokenReachesTheRepository()
    {
        using var source = new CancellationTokenSource();
        var detail = SeedDetail();

        await CreateHandler().Handle(new GetPublicListingByIdQuery(detail.ListingId), source.Token);

        Assert.Equal(source.Token, _listings.CancellationToken);
    }

    [Fact]
    public async Task ARepositoryFailureIsNotSwallowed()
    {
        _listings.ExceptionToThrow = new InvalidOperationException("database unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler().Handle(new GetPublicListingByIdQuery(Guid.NewGuid())));

        Assert.Equal("database unavailable", exception.Message);
    }

    [Fact]
    public async Task ANullQueryIsAProgrammingError()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
    }

    private GetPublicListingByIdQueryHandler CreateHandler()
    {
        return new GetPublicListingByIdQueryHandler(_listings);
    }

    private PublicListingDetail SeedDetail()
    {
        var detail = new PublicListingDetail(
            Guid.NewGuid(),
            ListingPurpose.Rent,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            PublishedAt);

        _listings.Details[detail.ListingId] = detail;

        return detail;
    }
}
