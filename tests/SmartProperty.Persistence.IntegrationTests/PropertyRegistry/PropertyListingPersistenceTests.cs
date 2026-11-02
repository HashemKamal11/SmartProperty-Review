using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Domain.Locations;
using NpgsqlTypes;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the market-facing listing facts, the views, and the AI sync state of a property survive real PostgreSQL,
/// that the database refuses what the domain would, and that the read models project them in one query.
/// </summary>
public sealed class PropertyListingPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    // ---- listing facts

    [Fact]
    public async Task EveryListingFactRoundTrips()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.StandaloneVilla,
            new PropertyAddress("EG"),
            CreatedAt,
            listing: new PropertyListingInfo(
                TransactionType.Rent, 85000.55m, 240.75m, 12, true, ListingStatus.Rented,
                "عنوان", "Title", "وصف طويل", "A long description"));

        await SaveAsync(property);

        var listing = (await ReloadAsync(property.Id)).Listing;
        Assert.Equal(TransactionType.Rent, listing.TransactionType);
        Assert.Equal(85000.55m, listing.Price);
        Assert.Equal(240.75m, listing.AreaSqm);
        Assert.Equal(12, listing.Floor);
        Assert.True(listing.Furnished);
        Assert.Equal(ListingStatus.Rented, listing.ListingStatus);
        Assert.Equal("عنوان", listing.TitleAr);
        Assert.Equal("Title", listing.TitleEn);
        Assert.Equal("وصف طويل", listing.DescriptionAr);
        Assert.Equal("A long description", listing.DescriptionEn);
    }

    [Fact]
    public async Task AnEmptyListingIsStoredAsNullsAndAvailable()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);

        await SaveAsync(property);

        Assert.Equal(PropertyListingInfo.Empty, (await ReloadAsync(property.Id)).Listing);
        Assert.Equal("Available", await ReadColumnAsync(property.Id, "listing_status"));
        Assert.Null(await ReadColumnAsync(property.Id, "price"));
        Assert.Null(await ReadColumnAsync(property.Id, "transaction_type"));
    }

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Apartment, "Apartment")]
    [InlineData(PropertyType.TwinHouse, "TwinHouse")]
    [InlineData(PropertyType.StandaloneVilla, "StandaloneVilla")]
    [InlineData(PropertyType.HotelApartment, "HotelApartment")]
    [InlineData(PropertyType.Warehouse, "Warehouse")]
    [InlineData(PropertyType.Farm, "Farm")]
    public async Task EachNewPropertyTypeIsStoredAsItsName(PropertyType type, string expected)
    {
        var property = new Property(Guid.NewGuid(), type, new PropertyAddress("EG"), CreatedAt);

        await SaveAsync(property);

        Assert.Equal(expected, await ReadColumnAsync(property.Id, "type"));
        Assert.Equal(type, (await ReloadAsync(property.Id)).Type);
    }

    [Fact]
    public async Task EveryEnumValueIsStoredByName()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt,
            finishingStatus: FinishingStatus.CoreAndShell,
            listing: new PropertyListingInfo(TransactionType.Sale, listingStatus: ListingStatus.Sold));

        await SaveAsync(property);

        Assert.Equal("CoreAndShell", await ReadColumnAsync(property.Id, "finishing_status"));
        Assert.Equal("Sale", await ReadColumnAsync(property.Id, "transaction_type"));
        Assert.Equal("Sold", await ReadColumnAsync(property.Id, "listing_status"));
        Assert.Equal("NotSynced", await ReadColumnAsync(property.Id, "ai_sync_status"));
    }

    [Fact]
    public async Task UpdatingTheListingPersistsTheChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt,
            listing: new PropertyListingInfo(TransactionType.Sale, 100m, 50m));
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            tracked.UpdateDetails(
                PropertyType.Apartment,
                new PropertyAddress("EG"),
                CreatedAt.AddHours(1),
                listing: new PropertyListingInfo(TransactionType.Rent, 9000m, 55m));
            await updating.SaveChangesAsync();
        }

        var listing = (await ReloadAsync(property.Id)).Listing;
        Assert.Equal(TransactionType.Rent, listing.TransactionType);
        Assert.Equal(9000m, listing.Price);
    }

    [Fact]
    public async Task APriceKeepsTwoDecimalPlaces()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt,
            listing: new PropertyListingInfo(price: 1234567890123.45m));

        await SaveAsync(property);

        Assert.Equal(1234567890123.45m, (await ReloadAsync(property.Id)).Listing.Price);
    }

    // ---- constraints, written with plain SQL around the domain

    [Theory]
    [InlineData("price", "0", "ck_registry_properties_price_positive")]
    [InlineData("price", "-1", "ck_registry_properties_price_positive")]
    [InlineData("area_sqm", "0", "ck_registry_properties_area_sqm_range")]
    [InlineData("area_sqm", "100000.01", "ck_registry_properties_area_sqm_range")]
    [InlineData("floor", "-6", "ck_registry_properties_floor_range")]
    [InlineData("floor", "301", "ck_registry_properties_floor_range")]
    public async Task OutOfRangeNumbersAreRefusedByTheDatabase(string column, string value, string constraint)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(extraColumn: column, extraValue: value));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Theory]
    [InlineData("price", "0.01")]
    [InlineData("area_sqm", "100000")]
    [InlineData("floor", "-5")]
    [InlineData("floor", "300")]
    public async Task BoundaryNumbersAreAcceptedByTheDatabase(string column, string value)
    {
        await InsertAsync(extraColumn: column, extraValue: value);
    }

    [Theory]
    [InlineData("transaction_type", "Buy", "ck_registry_properties_transaction_type")]
    [InlineData("transaction_type", "sale", "ck_registry_properties_transaction_type")]
    [InlineData("listing_status", "Gone", "ck_registry_properties_listing_status")]
    [InlineData("ai_sync_status", "Pending", "ck_registry_properties_ai_sync_status")]
    [InlineData("finishing_status", "Furnished", "ck_registry_properties_finishing_status")]
    public async Task UnsupportedNamesAreRefusedByTheDatabase(string column, string value, string constraint)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(extraColumn: column, extraValue: value, extraIsText: true));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Theory]
    [InlineData("transaction_type", "Sale")]
    [InlineData("transaction_type", "Rent")]
    [InlineData("listing_status", "Reserved")]
    [InlineData("ai_sync_status", "Failed")]
    [InlineData("finishing_status", "CoreAndShell")]
    public async Task SupportedNamesAreAcceptedByTheDatabase(string column, string value)
    {
        await InsertAsync(extraColumn: column, extraValue: value, extraIsText: true);
    }

    [Fact]
    public async Task ARowWrittenWithoutTheNewColumnsGetsTheDatabaseDefaults()
    {
        var id = await InsertAsync();

        Assert.Equal("Available", await ReadColumnAsync(id, "listing_status"));
        Assert.Equal("NotSynced", await ReadColumnAsync(id, "ai_sync_status"));
    }

    // ---- views

    [Fact]
    public async Task APropertysViewsRoundTripInDisplayOrder()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea", "City", "Garden"));

        await SaveAsync(property);

        Assert.Equal(["City", "Garden", "Sea"], await ReadViewCodesAsync(property.Id));
    }

    [Fact]
    public async Task UpdatingTheViewsAddsAndRemovesOnlyTheDifference()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("City", "Sea"));
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            var garden = await updating.PropertyViews.SingleAsync(v => v.Code == "Garden");
            var city = tracked.Views.Single(v => v.Code == "City");

            Assert.True(tracked.UpdateDetails(
                PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddHours(1), views: [city, garden]));
            await updating.SaveChangesAsync();
        }

        Assert.Equal(["City", "Garden"], await ReadViewCodesAsync(property.Id));
    }

    [Fact]
    public async Task ClearingTheViewsRemovesEveryLinkRow()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("City", "Sea"));
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            tracked.UpdateDetails(PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddHours(1));
            await updating.SaveChangesAsync();
        }

        Assert.Empty(await ReadViewCodesAsync(property.Id));
    }

    [Fact]
    public async Task ADuplicateViewCodeIsRefusedByTheDatabase()
    {
        await SeedViewsAsync();
        await using var context = Host.CreateVerificationContext();
        context.PropertyViews.Add(new PropertyView(Guid.NewGuid(), "Sea", "بحر", "Sea again", 99));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("ux_registry_views_code", postgres.ConstraintName);
    }

    [Fact]
    public async Task TheSameViewCannotBeLinkedTwiceAndALinkNeedsBothEnds()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea"));
        await SaveAsync(property);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO registry.property_views (property_id, view_id) SELECT property_id, view_id FROM registry.property_views;"));
        var noView = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.property_views (property_id, view_id) VALUES ('{property.Id}', '{Guid.NewGuid()}');"));
        var noProperty = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.property_views (property_id, view_id) SELECT '{Guid.NewGuid()}', id FROM registry.views LIMIT 1;"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, noView.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, noProperty.SqlState);
    }

    [Fact]
    public async Task AViewInUseCannotBeDeleted()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea"));
        await SaveAsync(property);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM registry.views WHERE code = 'Sea';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    // ---- AI sync state

    [Fact]
    public async Task TheSyncStateRoundTripsAndNeverMovesTheUpdatedInstant()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt);
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            tracked.MarkAiSynced(CreatedAt.AddMinutes(5));
            await updating.SaveChangesAsync();
        }

        var synced = await ReloadAsync(property.Id);
        Assert.Equal(AiSyncStatus.Synced, synced.AiSyncStatus);
        Assert.Equal(CreatedAt.AddMinutes(5), synced.AiSyncedAt);
        Assert.Null(synced.AiSyncError);
        Assert.Equal(CreatedAt, synced.UpdatedAt);
        Assert.Equal("Synced", await ReadColumnAsync(property.Id, "ai_sync_status"));

        await using (var failing = Host.CreateVerificationContext())
        {
            var tracked = await failing.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            tracked.MarkAiSyncFailed("the service timed out");
            await failing.SaveChangesAsync();
        }

        var failed = await ReloadAsync(property.Id);
        Assert.Equal(AiSyncStatus.Failed, failed.AiSyncStatus);
        Assert.Equal("the service timed out", failed.AiSyncError);
    }

    [Fact]
    public async Task ALongSyncErrorFitsTheColumn()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt);
        property.MarkAiSyncFailed(new string('e', Property.MaxAiSyncErrorLength + 400));

        await SaveAsync(property);

        Assert.Equal(Property.MaxAiSyncErrorLength, (await ReloadAsync(property.Id)).AiSyncError!.Length);
    }

    // ---- read models

    [Fact]
    public async Task GetDetailsByIdProjectsTheListingViewsAndSyncStateInOneCommand()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Penthouse, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea", "City"),
            listing: new PropertyListingInfo(TransactionType.Sale, 5_000_000m, 300m, 20, false));
        property.MarkAiSyncFailed("it broke");
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var details = await repository.GetDetailsByIdAsync(property.Id);

        Assert.NotNull(details);
        Assert.Equal(1, Host.Commands.Count);
        Assert.Equal(PropertyType.Penthouse, details.Type);
        Assert.Equal(["City", "Sea"], details.Views!.Select(v => v.Code));
        Assert.Equal("مدينة", details.Views![0].NameAr);
        Assert.Equal(TransactionType.Sale, details.Listing!.TransactionType);
        Assert.Equal(5_000_000m, details.Listing.Price);
        Assert.Equal(20, details.Listing.Floor);
        Assert.False(details.Listing.Furnished);
        Assert.Equal(ListingStatus.Available, details.Listing.ListingStatus);
        Assert.Equal(AiSyncStatus.Failed, details.AiSync!.Status);
        Assert.Equal("it broke", details.AiSync.Error);
    }

    [Fact]
    public async Task ListProjectsEachPropertysOwnViewsInTwoCommands()
    {
        await SeedViewsAsync();
        var withSea = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea"));
        var withTwo = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddMinutes(1),
            views: await LoadViewsAsync("Nile", "Garden"));
        var withNone = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddMinutes(2));
        await SaveAsync(withSea);
        await SaveAsync(withTwo);
        await SaveAsync(withNone);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var page = await repository.ListAsync(null, null, null, 0, 10);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(2, Host.Commands.Count);
        Assert.Empty(page.Items.Single(p => p.Id == withNone.Id).Views!);
        Assert.Equal(["Sea"], page.Items.Single(p => p.Id == withSea.Id).Views!.Select(v => v.Code));
        Assert.Equal(["Garden", "Nile"], page.Items.Single(p => p.Id == withTwo.Id).Views!.Select(v => v.Code));
    }

    [Fact]
    public async Task GetExistingIdsReturnsOnlyTheIdsThatAreProperties()
    {
        var one = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);
        var archived = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);
        archived.Archive(CreatedAt.AddMinutes(1));
        await SaveAsync(one);
        await SaveAsync(archived);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var existing = await repository.GetExistingIdsAsync([one.Id, archived.Id, Guid.NewGuid(), Guid.Empty]);

        Assert.Equal(new[] { one.Id, archived.Id }.Order(), existing.Order());
    }

    [Fact]
    public async Task GetByIdLoadsTheViewsSoTheyCanBeCompared()
    {
        await SeedViewsAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt,
            views: await LoadViewsAsync("Sea"));
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var tracked = await repository.GetByIdAsync(property.Id);

        Assert.NotNull(tracked);
        Assert.Equal(["Sea"], tracked.Views.Select(v => v.Code));
    }

    [Fact]
    public async Task TheViewRepositoryResolvesCodesAndListsEverything()
    {
        await SeedViewsAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IViewRepository>();

        var found = await repository.GetByCodesAsync(["Sea", "Moon", "City"]);
        var all = await repository.ListAsync();

        Assert.Equal(["City", "Sea"], found.Select(v => v.Code));
        Assert.Equal(["City", "Garden", "Nile", "Sea"], all.Select(v => v.Code));
        Assert.Equal("City", all[0].NameEn);
    }

    [Fact]
    public async Task AnAmenityCarriesItsAiCodeThroughTheDatabase()
    {
        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Amenities.Add(new Amenity(Guid.NewGuid(), "SwimmingPool", "مسبح", "Swimming pool", 1, "pool"));
            seeding.Amenities.Add(new Amenity(Guid.NewGuid(), "BarbecueArea", "منطقة شواء", "Barbecue area", 2));
            await seeding.SaveChangesAsync();
        }

        await using var reading = Host.CreateVerificationContext();
        var amenities = await reading.Amenities.AsNoTracking().OrderBy(a => a.SortOrder).ToListAsync();

        Assert.Equal("pool", amenities[0].AiCode);
        Assert.Null(amenities[1].AiCode);
    }

    // ---- place

    [Fact]
    public async Task APropertysPlaceRoundTripsThroughItsAreaAndCompound()
    {
        var (_, _, area, compound) = await SeedPlaceAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area, compound: compound);

        await SaveAsync(property);

        await using var reading = Host.CreateVerificationContext();
        var reloaded = await reading.Properties.AsNoTracking()
            .Include(p => p.Area!).ThenInclude(a => a.City).ThenInclude(c => c.Country)
            .Include(p => p.Compound)
            .SingleAsync(p => p.Id == property.Id);
        Assert.Equal(area.Id, reloaded.AreaId);
        Assert.Equal(compound.Id, reloaded.CompoundId);
        Assert.Equal(area.NameEn, reloaded.Area!.NameEn);
        Assert.Equal(area.City.NameEn, reloaded.Area.City.NameEn);
        Assert.Equal(area.City.Country.NameAr, reloaded.Area.City.Country.NameAr);
        Assert.Equal(compound.NameEn, reloaded.Compound!.NameEn);
    }

    [Fact]
    public async Task APropertyWithoutAPlaceStoresNullIds()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);

        await SaveAsync(property);

        Assert.Null(await ReadColumnAsync(property.Id, "area_id"));
        Assert.Null(await ReadColumnAsync(property.Id, "compound_id"));
    }

    [Fact]
    public async Task MovingAPropertyToAnotherAreaPersistsAndClearsTheCompoundToo()
    {
        var (_, city, area, compound) = await SeedPlaceAsync();
        var other = await AddAreaAsync(city);
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area, compound: compound);
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Views).Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            var moved = await updating.Areas.SingleAsync(a => a.Id == other.Id);

            Assert.True(tracked.UpdateDetails(PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddHours(1), area: moved));
            await updating.SaveChangesAsync();
        }

        Assert.Equal(other.Id, await ReadColumnAsync(property.Id, "area_id"));
        Assert.Null(await ReadColumnAsync(property.Id, "compound_id"));
    }

    [Fact]
    public async Task AnUnknownAreaOrCompoundIsRefusedByTheDatabase()
    {
        var unknownArea = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.properties (id, type, status, address_country_code, area_id, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'Villa', 'Active', 'EG', '{Guid.NewGuid()}', now(), now());"));

        var (_, _, area, _) = await SeedPlaceAsync();
        var unknownCompound = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.properties (id, type, status, address_country_code, area_id, compound_id, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'Villa', 'Active', 'EG', '{area.Id}', '{Guid.NewGuid()}', now(), now());"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownArea.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownCompound.SqlState);
    }

    [Fact]
    public async Task ACompoundWithoutItsAreaIsRefusedByTheDatabase()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.properties (id, type, status, address_country_code, compound_id, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'Villa', 'Active', 'EG', '{Guid.NewGuid()}', now(), now());"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_registry_properties_place", exception.ConstraintName);
    }

    [Fact]
    public async Task AnAreaInUseCannotBeDeleted()
    {
        var (_, _, area, _) = await SeedPlaceAsync();
        await SaveAsync(new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM locations.areas WHERE id = '{area.Id}';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task GetDetailsByIdProjectsThePlaceWithItsCityAndCountryInOneCommand()
    {
        var (country, city, area, compound) = await SeedPlaceAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area, compound: compound);
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var details = await repository.GetDetailsByIdAsync(property.Id);

        Assert.NotNull(details);
        Assert.Equal(1, Host.Commands.Count);
        Assert.Equal(country.Id, details.Place!.Country.Id);
        Assert.Equal(country.NameAr, details.Place.Country.NameAr);
        Assert.Equal(city.Id, details.Place.City.Id);
        Assert.Equal(area.Id, details.Place.Area.Id);
        Assert.Equal(area.NameEn, details.Place.Area.NameEn);
        Assert.Equal(compound.Id, details.Place.Compound!.Id);
    }

    [Fact]
    public async Task APropertyInAnAreaWithoutACompoundHasNoCompoundInItsPlace()
    {
        var (_, _, area, _) = await SeedPlaceAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area);
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var details = await repository.GetDetailsByIdAsync(property.Id);

        Assert.Equal(area.Id, details!.Place!.Area.Id);
        Assert.Null(details.Place.Compound);
    }

    [Fact]
    public async Task ListProjectsEachPropertysPlaceAndNullWhenItHasNone()
    {
        var (_, _, area, compound) = await SeedPlaceAsync();
        var located = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area, compound: compound);
        var unlocated = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt.AddMinutes(1));
        await SaveAsync(located);
        await SaveAsync(unlocated);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var page = await repository.ListAsync(null, null, null, 0, 10);

        Assert.Equal(2, Host.Commands.Count);
        Assert.Equal(area.Id, page.Items.Single(p => p.Id == located.Id).Place!.Area.Id);
        Assert.Null(page.Items.Single(p => p.Id == unlocated.Id).Place);
    }

    [Fact]
    public async Task GetByIdLoadsTheAreaItsCityAndCountryAndTheCompound()
    {
        var (_, _, area, compound) = await SeedPlaceAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, new PropertyAddress("EG"), CreatedAt, area: area, compound: compound);
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();

        var tracked = await repository.GetByIdAsync(property.Id);

        Assert.NotNull(tracked);
        Assert.Equal(area.AiId, tracked.Area!.AiId);
        Assert.Equal(area.City.Country.NameEn, tracked.Area.City.Country.NameEn);
        Assert.Equal(compound.AiId, tracked.Compound!.AiId);
    }

    private async Task<(Country Country, City City, Area Area, Compound Compound)> SeedPlaceAsync()
    {
        var aiId = Random.Shared.Next(1_000, 1_000_000);
        var country = new Country(Guid.NewGuid(), aiId, "Egypt", "مصر");
        var city = new City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new Area(Guid.NewGuid(), aiId, city, "Helwan", "حلوان");
        var compound = new Compound(Guid.NewGuid(), aiId, area, "Garden Residence", "جاردن ريزيدنس");

        await using var context = Host.CreateVerificationContext();
        context.Compounds.Add(compound);
        await context.SaveChangesAsync();

        return (country, city, area, compound);
    }

    private async Task<Area> AddAreaAsync(City city)
    {
        var area = new Area(Guid.NewGuid(), Random.Shared.Next(1_000_000, 2_000_000), city.Id, "Maadi", "المعادي");

        await using var context = Host.CreateVerificationContext();
        context.Areas.Add(area);
        await context.SaveChangesAsync();

        return area;
    }

    // ---- helpers

    private async Task SeedViewsAsync()
    {
        await using var context = Host.CreateVerificationContext();
        context.PropertyViews.AddRange(
            new PropertyView(Guid.NewGuid(), "City", "مدينة", "City", 1),
            new PropertyView(Guid.NewGuid(), "Garden", "حديقة", "Garden", 3),
            new PropertyView(Guid.NewGuid(), "Nile", "نيل", "Nile", 5),
            new PropertyView(Guid.NewGuid(), "Sea", "بحر", "Sea", 7));
        await context.SaveChangesAsync();
    }

    private async Task<PropertyView[]> LoadViewsAsync(params string[] codes)
    {
        await using var context = Host.CreateVerificationContext();
        var views = await context.PropertyViews.AsNoTracking().Where(v => codes.Contains(v.Code)).ToArrayAsync();
        Assert.Equal(codes.Length, views.Length);

        return views;
    }

    // Detached views are attached by key, so saving the property links them rather than inserting new rows.
    private async Task SaveAsync(Property property)
    {
        await using var writing = Host.CreateVerificationContext();
        foreach (var view in property.Views)
        {
            writing.Attach(view);
        }

        foreach (var amenity in property.Amenities)
        {
            writing.Attach(amenity);
        }

        if (property.Area is { } area)
        {
            writing.Attach(area);
        }

        if (property.Compound is { } compound)
        {
            writing.Attach(compound);
        }

        writing.Properties.Add(property);
        await writing.SaveChangesAsync();
    }

    private async Task<Property> ReloadAsync(Guid id)
    {
        await using var reading = Host.CreateVerificationContext();
        return await reading.Properties.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<string[]> ReadViewCodesAsync(Guid propertyId)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT v.code
            FROM registry.property_views pv
            JOIN registry.views v ON v.id = pv.view_id
            WHERE pv.property_id = @id
            ORDER BY v.sort_order;
            """,
            connection);
        command.Parameters.AddWithValue("id", propertyId);

        var codes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes.ToArray();
    }

    // The column name is always a literal from this class, never input.
    private async Task<object?> ReadColumnAsync(Guid id, string column)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"SELECT {column} FROM registry.properties WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", id);

        var value = await command.ExecuteScalarAsync();

        return value is DBNull ? null : value;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Writes one row with plain SQL, deliberately around the domain, setting at most one extra column. The column
    /// name is always a literal from this class, never input.
    /// </summary>
    private async Task<Guid> InsertAsync(string? extraColumn = null, string? extraValue = null, bool extraIsText = false)
    {
        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        var columns = "id, type, status, address_country_code, created_at, updated_at";
        var values = "@id, 'Apartment', 'Active', 'EG', @at, @at";
        if (extraColumn is not null)
        {
            columns += $", {extraColumn}";
            values += ", @extra";
        }

        await using var command = new NpgsqlCommand($"INSERT INTO registry.properties ({columns}) VALUES ({values});", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("at", CreatedAt);

        if (extraColumn is not null)
        {
            command.Parameters.Add(extraIsText
                ? new NpgsqlParameter("extra", NpgsqlDbType.Text) { Value = extraValue }
                : new NpgsqlParameter("extra", NpgsqlDbType.Numeric) { Value = decimal.Parse(extraValue!, System.Globalization.CultureInfo.InvariantCulture) });
        }

        await command.ExecuteNonQueryAsync();

        return id;
    }
}
