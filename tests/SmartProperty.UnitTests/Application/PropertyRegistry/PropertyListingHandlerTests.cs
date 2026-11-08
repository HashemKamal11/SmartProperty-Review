using SmartProperty.Application.PropertyRegistry.Archive;
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;
using static SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

/// <summary>
/// Covers the views, the market-facing listing fields, the AI vocabulary in the commands, and the calls the
/// handlers make to the AI synchronizer.
/// </summary>
public sealed class PropertyListingHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeAmenityRepository _amenities = new();
    private readonly FakeViewRepository _views = new();
    private readonly FakeLocationRepository _locations = new();
    private readonly FakePropertyAiSynchronizer _ai = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeDateTimeProvider _clock = new(Now);

    public PropertyListingHandlerTests()
    {
        // The complete test property sits in Helwan (compound: Garden Residence); the handler must find them.
        _locations.Areas.Add(Helwan);
        _locations.Compounds.Add(GardenResidence);
        _locations.Areas.Add(Maadi);
    }

    // ---- create

    [Fact]
    public async Task CreateStoresEveryListingFieldAndReturnsThem()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Penthouse",
            Helwan.Id,
            TransactionType: "Rent",
            Price: 85000m,
            AreaSqm: 240m,
            Floor: 12,
            Furnished: true,
            ListingStatus: "Reserved",
            TitleAr: "  بنتهاوس  ",
            TitleEn: " Penthouse ",
            DescriptionAr: "وصف",
            DescriptionEn: "Description",
            CompoundId: GardenResidence.Id));

        Assert.True(result.IsSuccess);
        var property = Assert.Single(_properties.Added);
        Assert.Equal(PropertyType.Penthouse, property.Type);
        Assert.Equal(TransactionType.Rent, property.Listing.TransactionType);
        Assert.Equal(85000m, property.Listing.Price);
        Assert.Equal(240m, property.Listing.AreaSqm);
        Assert.Equal(12, property.Listing.Floor);
        Assert.True(property.Listing.Furnished);
        Assert.Equal(ListingStatus.Reserved, property.Listing.ListingStatus);
        Assert.Equal("بنتهاوس", property.Listing.TitleAr);
        Assert.Equal("Penthouse", property.Listing.TitleEn);
        Assert.Equal(Helwan.Id, property.AreaId);
        Assert.Equal(GardenResidence.Id, property.CompoundId);

        var listing = result.Value.Listing!;
        Assert.Equal(TransactionType.Rent, listing.TransactionType);
        Assert.Equal(85000m, listing.Price);
        Assert.Equal(ListingStatus.Reserved, listing.ListingStatus);
        Assert.Equal(Helwan.Id, result.Value.Place!.Area.Id);
        Assert.Equal("Cairo", result.Value.Place.City.NameEn);
        Assert.Equal("مصر", result.Value.Place.Country.NameAr);
        Assert.Equal(GardenResidence.Id, result.Value.Place.Compound!.Id);
    }

    [Fact]
    public async Task CreateWithoutListingFieldsIsAvailableAndEmpty()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", Helwan.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyListingInfo.Empty, Assert.Single(_properties.Added).Listing);
        Assert.Equal(ListingStatus.Available, result.Value.Listing!.ListingStatus);
        Assert.Null(result.Value.Listing.Price);
    }

    [Fact]
    public async Task CreateAttachesTheChosenViewsInDisplayOrder()
    {
        _views.Seed("Sea", 7);
        _views.Seed("City", 1);

        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Villa", Helwan.Id, ViewCodes: ["Sea", "City", "Sea"]));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, Assert.Single(_properties.Added).Views.Count);
        Assert.Equal(["City", "Sea"], result.Value.Views!.Select(view => view.Code));
    }

    [Fact]
    public async Task CreateRejectsAnUnknownViewCodeNamingItAndStoresNothing()
    {
        _views.Seed("Sea", 7);

        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Villa", Helwan.Id, ViewCodes: ["Sea", "Moon"]));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("Moon", result.Error.Description);
        Assert.Contains("view", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Empty(_ai.Synced);
    }

    [Fact]
    public async Task CreateRejectsABlankViewCode()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Villa", Helwan.Id, ViewCodes: [" "]));

        Assert.True(result.IsFailure);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task CreateOffersThePropertyToTheAiServiceOnceAfterItIsCommitted()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Helwan.Id));

        Assert.True(result.IsSuccess);
        Assert.Same(Assert.Single(_properties.Added), Assert.Single(_ai.Synced));
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateDoesNotOfferAPropertyThatWasRefused()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Helwan.Id, Price: -1m));

        Assert.True(result.IsFailure);
        Assert.Empty(_ai.Synced);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task CreateReturnsTheSyncStateTheSynchronizerLeftOnTheProperty()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Helwan.Id));

        Assert.Equal(AiSyncStatus.NotSynced, result.Value.AiSync!.Status);
    }

    [Theory]
    [InlineData("Unit")]
    [InlineData("apartment")]
    [InlineData(" Apartment")]
    [InlineData("3")]
    [InlineData("Twin House")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateAcceptsOnlyTheExactTypeNames(string? type)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(type, Helwan.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("Apartment", result.Error.Description);
    }

    [Theory]
    [InlineData("Land")]
    [InlineData("Building")]
    [InlineData("Apartment")]
    [InlineData("Villa")]
    [InlineData("Duplex")]
    [InlineData("Studio")]
    [InlineData("Chalet")]
    [InlineData("Townhouse")]
    [InlineData("TwinHouse")]
    [InlineData("Penthouse")]
    [InlineData("StandaloneVilla")]
    [InlineData("HotelApartment")]
    [InlineData("Office")]
    [InlineData("Shop")]
    [InlineData("Warehouse")]
    [InlineData("Farm")]
    public async Task CreateAcceptsEverySixteenAiType(string type)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(type, Helwan.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(type, result.Value.Type.ToString());
    }

    [Theory]
    [InlineData("Finished")]
    [InlineData("LuxuryFinished")]
    [InlineData("SemiFinished")]
    [InlineData("CoreAndShell")]
    public async Task CreateAcceptsEveryFinishingStatus(string status)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Helwan.Id, FinishingStatus: status));

        Assert.True(result.IsSuccess);
        Assert.Equal(status, result.Value.FinishingStatus.ToString());
    }

    [Theory]
    [InlineData("Unfinished")]
    [InlineData("FullyFinished")]
    [InlineData("SuperLux")]
    [InlineData("Furnished")]
    [InlineData("1")]
    [InlineData("finished")]
    [InlineData("Luxury Finished")]
    public async Task CreateRefusesTheOldAndMisspelledFinishingStatuses(string status)
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Apartment", Helwan.Id, FinishingStatus: status));

        Assert.True(result.IsFailure);
        Assert.Contains("CoreAndShell", result.Error!.Description);
    }

    public static TheoryData<CreatePropertyCommand, string> InvalidListings => new()
    {
        { new CreatePropertyCommand("Apartment", Helwan.Id, TransactionType: "Buy"), "Transaction type" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, TransactionType: "sale"), "Transaction type" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, ListingStatus: "Gone"), "Listing status" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, Price: 0m), "Price" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, Price: -10m), "Price" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, AreaSqm: 0m), "Area" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, AreaSqm: 100001m), "Area" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, Floor: -6), "Floor" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, Floor: 301), "Floor" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, TitleAr: new string('t', 301)), "title" },
        { new CreatePropertyCommand("Apartment", Helwan.Id, DescriptionEn: new string('d', 10001)), "description" }
    };

    [Theory]
    [MemberData(nameof(InvalidListings))]
    public async Task CreateRejectsAnInvalidListingFieldAndStoresNothing(CreatePropertyCommand command, string mention)
    {
        var result = await CreateHandler().Handle(command);

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains(mention, result.Error.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    // ---- update

    [Fact]
    public async Task UpdateReplacesTheListingAndTheViewsAndOffersTheChangeToTheAiService()
    {
        var sea = _views.Seed("Sea", 7);
        var property = SeedProperty();

        var result = await UpdateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "Villa",
            Maadi.Id,
            Bedrooms: 5,
            Bathrooms: 4,
            ViewCodes: ["Sea"],
            TransactionType: "Sale",
            Price: 9_000_000m,
            AreaSqm: 400m));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyType.Villa, property.Type);
        Assert.Equal(9_000_000m, property.Listing.Price);
        Assert.Contains(sea, property.Views);
        Assert.Equal(["Sea"], result.Value.Views!.Select(view => view.Code));
        Assert.Equal(9_000_000m, result.Value.Listing!.Price);
        Assert.Same(property, Assert.Single(_ai.Synced));
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateWithoutListingFieldsClearsThem()
    {
        var property = SeedProperty(complete: true);

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "TwinHouse", Helwan.Id, Bedrooms: 4, Bathrooms: 3));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyListingInfo.Empty, property.Listing);
    }

    [Fact]
    public async Task AnUnchangedUpdateOfAnAlreadySyncedPropertyDoesNotCallTheAiServiceAgain()
    {
        var property = SeedProperty(complete: true);
        property.MarkAiSynced(CreatedAt);

        var result = await UpdateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "TwinHouse",
            Helwan.Id,
            Bedrooms: 4,
            Bathrooms: 3,
            FinishingStatus: "LuxuryFinished",
            TransactionType: "Sale",
            Price: 6_250_000.50m,
            AreaSqm: 210.5m,
            Floor: 2,
            Furnished: true,
            ListingStatus: "Reserved",
            TitleAr: "عنوان",
            TitleEn: "Title",
            DescriptionAr: "وصف",
            DescriptionEn: "Description",
            CompoundId: GardenResidence.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_ai.Synced);
    }

    [Theory]
    [InlineData(AiSyncStatus.NotSynced)]
    [InlineData(AiSyncStatus.Failed)]
    public async Task AnUnchangedUpdateOfAPropertyTheAiServiceDoesNotHoldRepairsItByOfferingItAgain(AiSyncStatus status)
    {
        var property = SeedProperty(complete: true);
        if (status == AiSyncStatus.Failed)
        {
            property.MarkAiSyncFailed("earlier failure");
        }

        var result = await UpdateHandler().Handle(new UpdatePropertyCommand(
            property.Id,
            "TwinHouse",
            Helwan.Id,
            Bedrooms: 4,
            Bathrooms: 3,
            FinishingStatus: "LuxuryFinished",
            TransactionType: "Sale",
            Price: 6_250_000.50m,
            AreaSqm: 210.5m,
            Floor: 2,
            Furnished: true,
            ListingStatus: "Reserved",
            TitleAr: "عنوان",
            TitleEn: "Title",
            DescriptionAr: "وصف",
            DescriptionEn: "Description",
            CompoundId: GardenResidence.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Same(property, Assert.Single(_ai.Synced));
    }

    [Fact]
    public async Task AnInvalidUpdateNeverReachesTheAiService()
    {
        var property = SeedProperty();

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "Apartment", Helwan.Id, Price: -1m));

        Assert.True(result.IsFailure);
        Assert.Empty(_ai.Synced);
    }

    [Fact]
    public async Task UpdateRejectsAnUnknownViewCodeAndLeavesThePropertyUntouched()
    {
        var property = SeedProperty();

        var result = await UpdateHandler().Handle(
            new UpdatePropertyCommand(property.Id, "Apartment", Helwan.Id, ViewCodes: ["Moon"]));

        Assert.True(result.IsFailure);
        Assert.Contains("Moon", result.Error!.Description);
        Assert.Empty(property.Views);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    // ---- place

    [Fact]
    public async Task CreateWithoutAnAreaIsRejectedAndStoresNothing()
    {
        var missing = await CreateHandler().Handle(new CreatePropertyCommand("Land", null));
        var empty = await CreateHandler().Handle(new CreatePropertyCommand("Land", Guid.Empty));

        Assert.True(missing.IsFailure);
        Assert.True(empty.IsFailure);
        Assert.Equal("validation.failed", missing.Error!.Code);
        Assert.Contains("Area is required", missing.Error.Description);
        Assert.Contains("Area is required", empty.Error!.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateRejectsAnAreaWhoseCountryHasNoCodeAndStoresNothing()
    {
        var atlantis = _locations.SeedArea(_locations.SeedCity(_locations.SeedCountry(9, "Atlantis", "أطلنطس")));

        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", atlantis.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("'Atlantis' has no country code", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CreateBuildsTheAddressFromTheAreaItsCityAndItsCountry()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand(
            "Land", Maadi.Id, AddressLine: " 9 Road ", PostalCode: " 11728 "));

        Assert.True(result.IsSuccess);
        var address = Assert.Single(_properties.Added).Address;
        Assert.Equal("EG", address.CountryCode);
        Assert.Equal("Cairo", address.City);
        Assert.Equal("Maadi", address.District);
        Assert.Null(address.Region);
        Assert.Equal("9 Road", address.AddressLine);
        Assert.Equal("11728", address.PostalCode);
    }

    [Fact]
    public async Task CreateLocatesThePropertyByAreaAndReturnsTheCityAndCountryAboveIt()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", Maadi.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(Maadi.Id, Assert.Single(_properties.Added).AreaId);
        Assert.Null(Assert.Single(_properties.Added).CompoundId);
        Assert.Equal("Maadi", result.Value.Place!.Area.NameEn);
        Assert.Equal("Cairo", result.Value.Place.City.NameEn);
        Assert.Equal("Egypt", result.Value.Place.Country.NameEn);
        Assert.Null(result.Value.Place.Compound);
    }

    [Fact]
    public async Task CreateRejectsAnUnknownAreaAndStoresNothing()
    {
        var result = await CreateHandler().Handle(new CreatePropertyCommand("Land", Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal("Unknown area.", result.Error.Description);
        Assert.Empty(_properties.Added);
        Assert.Empty(_ai.Synced);
    }

    [Fact]
    public async Task CreateRejectsAnUnknownCompound()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Land", Helwan.Id, CompoundId: Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("Unknown compound.", result.Error!.Description);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task CreateRejectsACompoundWithoutItsArea()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Land", null, CompoundId: GardenResidence.Id));

        Assert.True(result.IsFailure);
        Assert.Contains("Area is required", result.Error!.Description);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task CreateRejectsACompoundThatBelongsToAnotherArea()
    {
        var result = await CreateHandler().Handle(
            new CreatePropertyCommand("Land", Maadi.Id, CompoundId: GardenResidence.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("The compound does not belong to the area.", result.Error!.Description);
        Assert.Empty(_properties.Added);
    }

    [Fact]
    public async Task UpdateMovesThePropertyAndRejectsAnUnknownAreaWithoutChangingIt()
    {
        var property = SeedProperty(complete: true);

        var moved = await UpdateHandler().Handle(CompleteUpdate(property.Id) with { AreaId = Maadi.Id, CompoundId = null });
        var refused = await UpdateHandler().Handle(CompleteUpdate(property.Id) with { AreaId = Guid.NewGuid(), CompoundId = null });

        Assert.True(moved.IsSuccess);
        Assert.Equal(Maadi.Id, property.AreaId);
        Assert.Equal("Maadi", moved.Value.Place!.Area.NameEn);
        Assert.True(refused.IsFailure);
        Assert.Equal("Unknown area.", refused.Error!.Description);
        Assert.Equal(Maadi.Id, property.AreaId);
    }

    [Fact]
    public async Task UpdateWithoutAnAreaIsRejectedAndKeepsThePlace()
    {
        var property = SeedProperty(complete: true);

        var missing = await UpdateHandler().Handle(CompleteUpdate(property.Id) with { AreaId = null, CompoundId = null });
        var empty = await UpdateHandler().Handle(CompleteUpdate(property.Id) with { AreaId = Guid.Empty, CompoundId = null });

        Assert.True(missing.IsFailure);
        Assert.True(empty.IsFailure);
        Assert.Equal("validation.failed", missing.Error!.Code);
        Assert.Contains("Area is required", missing.Error.Description);
        Assert.Contains("Area is required", empty.Error!.Description);
        Assert.Equal(Helwan.Id, property.AreaId);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UpdateRejectsAnAreaWhoseCountryHasNoCodeAndKeepsThePlace()
    {
        var property = SeedProperty(complete: true);
        var atlantis = _locations.SeedArea(_locations.SeedCity(_locations.SeedCountry(9, "Atlantis", "أطلنطس")));

        var result = await UpdateHandler().Handle(
            CompleteUpdate(property.Id) with { AreaId = atlantis.Id, CompoundId = null });

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Contains("'Atlantis' has no country code", result.Error.Description);
        Assert.Equal(Helwan.Id, property.AreaId);
        Assert.Equal("EG", property.Address.CountryCode);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    private static UpdatePropertyCommand CompleteUpdate(Guid id)
    {
        return new UpdatePropertyCommand(
            id,
            "TwinHouse",
            Helwan.Id,
            Bedrooms: 4,
            Bathrooms: 3,
            FinishingStatus: "LuxuryFinished",
            TransactionType: "Sale",
            Price: 6_250_000.50m,
            AreaSqm: 210.5m,
            Floor: 2,
            Furnished: true,
            ListingStatus: "Reserved",
            TitleAr: "عنوان",
            TitleEn: "Title",
            DescriptionAr: "وصف",
            DescriptionEn: "Description",
            CompoundId: GardenResidence.Id);
    }

    // ---- archive

    [Fact]
    public async Task ArchiveTakesThePropertyOffTheAiServiceAfterArchivingIt()
    {
        var property = SeedProperty(complete: true);

        var result = await new ArchivePropertyCommandHandler(_properties, _ai, _clock, _unitOfWork)
            .Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Same(property, Assert.Single(_ai.Removed));
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnArchiveThatIsRefusedDoesNotTouchTheAiService()
    {
        var property = SeedProperty(complete: true);
        property.Archive(CreatedAt.AddMinutes(1));

        var result = await new ArchivePropertyCommandHandler(_properties, _ai, _clock, _unitOfWork)
            .Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsFailure);
        Assert.Empty(_ai.Removed);
    }

    private Property SeedProperty(bool complete = false)
    {
        var property = complete ? Complete() : Bare();
        _properties.Tracked.Add(property.Id, property);

        return property;
    }

    private CreatePropertyCommandHandler CreateHandler()
    {
        return new CreatePropertyCommandHandler(_properties, _amenities, _views, _locations, _ai, _clock, _unitOfWork);
    }

    private UpdatePropertyCommandHandler UpdateHandler()
    {
        return new UpdatePropertyCommandHandler(_properties, _amenities, _views, _locations, _ai, _clock, _unitOfWork);
    }
}
