using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Create;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// The public marketplace reads against real PostgreSQL: every pricing option and the availability terms of a rental,
/// searching by any offered pricing period and by a stay, and the dates a commitment holds.
/// </summary>
public sealed class PublicRentalSearchPersistenceTests(PostgreSqlFixture fixture) : MarketplaceWorkflowTestBase(fixture)
{
    private static PublicListingSearchCriteria Rent(
        RentPeriod? period = null,
        decimal? min = null,
        decimal? max = null,
        string? currency = null,
        DateOnly? from = null,
        DateOnly? to = null,
        PublicListingSortOrder sort = PublicListingSortOrder.Newest) =>
        new(ListingPurpose.Rent, null, null, null, null, null, min, max, currency, period, sort, from, to);

    [Fact]
    public async Task ARentalsDetailShowsEveryPricingOptionAndItsAvailabilityTerms()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var detail = await DetailAsync(rent.ListingId);

        Assert.NotNull(detail);
        Assert.Equal(
            new[] { (RentPeriod.Monthly, 20_000m), (RentPeriod.Daily, 1_000m) }.OrderBy(option => option.Item1),
            detail.PricingOptions!.Select(option => (option.RentPeriod!.Value, option.Amount)).OrderBy(option => option.Item1));
        Assert.All(detail.PricingOptions!, option => Assert.Equal("EGP", option.Currency));
        Assert.Equal("Africa/Cairo", detail.RentalTerms!.TimeZoneId);
        Assert.Equal(new TimeOnly(14, 0), detail.RentalTerms.CheckInTime);
        Assert.Equal(new TimeOnly(11, 0), detail.RentalTerms.CheckOutTime);
        Assert.Null(detail.RentalTerms.MinimumStayDays);
        var window = Assert.Single(detail.RentalTerms.Windows);
        Assert.Equal(new DateOnly(2027, 6, 1), window.Start);
        Assert.Equal(new DateOnly(2027, 9, 1), window.End);
        Assert.Null(detail.BlockedRanges);
    }

    [Fact]
    public async Task ASaleHasNoOptionsTermsOrBlockedDates()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);

        var detail = await DetailAsync(sale.ListingId);

        Assert.NotNull(detail);
        Assert.Null(detail.PricingOptions);
        Assert.Null(detail.RentalTerms);
        Assert.Null(detail.BlockedRanges);
    }

    [Fact]
    public async Task ARentalIsFoundByAnyPricingPeriodItOffersNotOnlyItsPrimaryOne()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var daily = await SearchAsync(Rent(RentPeriod.Daily));
        var monthly = await SearchAsync(Rent(RentPeriod.Monthly));
        var yearly = await SearchAsync(Rent(RentPeriod.Yearly));
        var weekly = await SearchAsync(Rent(RentPeriod.Weekly));

        Assert.Contains(daily.Items, item => item.ListingId == rent.ListingId);
        Assert.Contains(monthly.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(yearly.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(weekly.Items, item => item.ListingId == rent.ListingId);
    }

    [Fact]
    public async Task APriceIsComparedInTheRequestedPeriodAndShownThatWay()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var dailyInRange = await SearchAsync(Rent(RentPeriod.Daily, min: 900m, max: 1_100m, currency: "EGP"));
        var dailyTooHigh = await SearchAsync(Rent(RentPeriod.Daily, min: 5_000m, max: 9_000m, currency: "EGP"));
        var monthlyAtDailyPrice = await SearchAsync(Rent(RentPeriod.Monthly, min: 900m, max: 1_100m, currency: "EGP"));
        var otherCurrency = await SearchAsync(Rent(RentPeriod.Daily, min: 900m, max: 1_100m, currency: "USD"));

        var shown = Assert.Single(dailyInRange.Items, item => item.ListingId == rent.ListingId);
        Assert.Equal(1_000m, shown.Price.Amount);
        Assert.Equal(RentPeriod.Daily, shown.Price.RentPeriod);
        Assert.DoesNotContain(dailyTooHigh.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(monthlyAtDailyPrice.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(otherCurrency.Items, item => item.ListingId == rent.ListingId);
        Assert.Equal(2, shown.PricingOptions!.Count);
    }

    [Fact]
    public async Task PriceSortingUsesTheRequestedPeriod()
    {
        var first = await SeedAsync([ListingPurpose.Rent]);
        var second = await SeedAsync([ListingPurpose.Rent]);
        var a = await PublishAsync(first, ListingPurpose.Rent);
        var b = await PublishAsync(second, ListingPurpose.Rent);

        var page = await SearchAsync(Rent(RentPeriod.Daily, currency: "EGP", sort: PublicListingSortOrder.PriceAsc));

        Assert.Contains(page.Items, item => item.ListingId == a.ListingId);
        Assert.Contains(page.Items, item => item.ListingId == b.ListingId);
        Assert.Equal(page.Items.Select(item => item.Price.Amount).Order(), page.Items.Select(item => item.Price.Amount));
    }

    [Fact]
    public async Task AStayMustFitAnAvailabilityWindow()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var inside = await SearchAsync(Rent(from: new DateOnly(2027, 6, 10), to: new DateOnly(2027, 6, 20)));
        var wholeWindow = await SearchAsync(Rent(from: new DateOnly(2027, 6, 1), to: new DateOnly(2027, 9, 1)));
        var startsBefore = await SearchAsync(Rent(from: new DateOnly(2027, 5, 30), to: new DateOnly(2027, 6, 5)));
        var endsAfter = await SearchAsync(Rent(from: new DateOnly(2027, 8, 30), to: new DateOnly(2027, 9, 3)));

        Assert.Contains(inside.Items, item => item.ListingId == rent.ListingId);
        Assert.Contains(wholeWindow.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(startsBefore.Items, item => item.ListingId == rent.ListingId);
        Assert.DoesNotContain(endsAfter.Items, item => item.ListingId == rent.ListingId);
    }

    [Fact]
    public async Task DatesHeldByACommitmentAreBlockedInTheDetailAndExcludedFromAvailabilitySearch()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var first = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewCustomerAsync();
        var requested = await RentalRequestAsync(customer, first.ListingId, new DateOnly(2027, 6, 10), new DateOnly(2027, 6, 20));
        Assert.True(requested.IsSuccess, requested.Error?.Code);
        Assert.True((await AcceptAsync(world.Owner, requested.Value.RequestId)).IsSuccess);

        // Accepting closed the advertisement. Advertising again is an explicit new version.
        var again = await PublishAsync(world, ListingPurpose.Rent);

        var detail = await DetailAsync(again.ListingId);
        var overlapping = await SearchAsync(Rent(from: new DateOnly(2027, 6, 15), to: new DateOnly(2027, 6, 25)));
        var adjacent = await SearchAsync(Rent(from: new DateOnly(2027, 6, 20), to: new DateOnly(2027, 6, 25)));
        var before = await SearchAsync(Rent(from: new DateOnly(2027, 6, 1), to: new DateOnly(2027, 6, 10)));

        var blocked = Assert.Single(detail!.BlockedRanges!);
        Assert.Equal(new DateOnly(2027, 6, 10), blocked.Start);
        Assert.Equal(new DateOnly(2027, 6, 20), blocked.End);
        Assert.DoesNotContain(overlapping.Items, item => item.ListingId == again.ListingId);
        Assert.Contains(adjacent.Items, item => item.ListingId == again.ListingId);
        Assert.Contains(before.Items, item => item.ListingId == again.ListingId);
    }

    [Fact]
    public async Task AnUnsignedCommitmentPastItsDeadlineNoLongerBlocksDatesInThePublicRead()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var first = await PublishAsync(world, ListingPurpose.Rent);
        var requested = await RentalRequestAsync(await NewCustomerAsync(), first.ListingId, new DateOnly(2027, 6, 10), new DateOnly(2027, 6, 20));
        Assert.True((await AcceptAsync(world.Owner, requested.Value.RequestId)).IsSuccess);
        var again = await PublishAsync(world, ListingPurpose.Rent);
        Assert.Single((await DetailAsync(again.ListingId))!.BlockedRanges!);

        // The deadline passes in real time; the stored status is still pending, and the read ignores the commitment.
        await ExecuteAsync("UPDATE marketplace.property_commitments SET signing_deadline = now() - interval '1 hour', accepted_at = now() - interval '2 days', updated_at = now() - interval '2 days'");

        Assert.Null((await DetailAsync(again.ListingId))!.BlockedRanges);
        Assert.Contains(
            (await SearchAsync(Rent(from: new DateOnly(2027, 6, 12), to: new DateOnly(2027, 6, 14)))).Items,
            item => item.ListingId == again.ListingId);
    }

    [Fact]
    public async Task AvailabilityDatesWithAnotherPurposeOrOnlyOneDateAreRefused()
    {
        await using var scope = Scope();
        var repository = scope.Get<IPublicListingReadRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(
            new PublicListingSearchCriteria(ListingPurpose.Sale, null, null, null, null, null, null, null, null, null,
                PublicListingSortOrder.Newest, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5)), 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(
            Rent(from: new DateOnly(2027, 6, 1)), 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(
            Rent(from: new DateOnly(2027, 6, 5), to: new DateOnly(2027, 6, 5)), 0, 10));
    }

    // ---- helpers

    private async Task<ListingDraftResult> PublishAsync(World world, ListingPurpose purpose)
    {
        var draft = await SubmittedAsync(world, purpose);
        var approved = await ApproveAsync(world, draft.SubmissionId);
        Assert.True(approved.IsSuccess, approved.Error?.Code);

        return draft;
    }

    private async Task<Guid> NewCustomerAsync()
    {
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Casey", "Customer", at);
        user.Activate(at);
        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<PublicListingDetail?> DetailAsync(Guid listingId)
    {
        await using var scope = Scope();

        return await scope.Get<IPublicListingReadRepository>().GetByIdAsync(listingId);
    }

    private async Task<PublicListingPage> SearchAsync(PublicListingSearchCriteria criteria)
    {
        await using var scope = Scope();

        return await scope.Get<IPublicListingReadRepository>().SearchAsync(criteria, 0, 100);
    }

    private async Task<SmartProperty.Common.Results.Result<RequestCreatedResult>> RentalRequestAsync(
        Guid customer, Guid listingId, DateOnly start, DateOnly end)
    {
        await using var scope = Scope();
        var handler = new CreateRentalRequestCommandHandler(
            new FakeCurrentUser(customer), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new CreateRentalRequestCommand(listingId, RentPeriod.Daily, start, end, null));
    }

    private async Task<SmartProperty.Common.Results.Result<AcceptRequestResult>> AcceptAsync(Guid owner, Guid requestId)
    {
        await using var scope = Scope();
        var handler = new AcceptRequestCommandHandler(
            new FakeCurrentUser(owner), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(),
            scope.Get<IMarketplaceSettingsRepository>(), Clock);

        return await handler.Handle(new AcceptRequestCommand(RequestKind.Rental, requestId));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
