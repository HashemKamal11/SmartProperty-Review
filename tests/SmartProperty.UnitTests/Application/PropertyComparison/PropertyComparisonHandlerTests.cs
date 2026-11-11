using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.PropertyComparison;
using SmartProperty.Application.PropertyComparison.Add;
using SmartProperty.Application.PropertyComparison.Get;
using SmartProperty.Application.PropertyComparison.Remove;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyComparison;

public sealed class PropertyComparisonHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePropertyRepository _properties = new();
    private readonly FakePropertyComparisonRepository _comparison = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly Guid _userId = Guid.NewGuid();

    public PropertyComparisonHandlerTests()
    {
        _users.Seed(new User(_userId, "buyer@example.com", "Buy", "Er", Now));
    }

    // ---- the item

    [Fact]
    public void AnItemNeedsRealIdsAndAPositionWithinTheLimit()
    {
        Assert.Throws<ArgumentException>(() => new PropertyComparisonItem(Guid.Empty, Guid.NewGuid(), 1, Now));
        Assert.Throws<ArgumentException>(() => new PropertyComparisonItem(Guid.NewGuid(), Guid.Empty, 1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyComparisonItem(Guid.NewGuid(), Guid.NewGuid(), 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyComparisonItem(Guid.NewGuid(), Guid.NewGuid(), 4, Now));
        Assert.Equal(3, PropertyComparisonItem.MaxItems);
        Assert.Equal(2, PropertyComparisonItem.MinItemsToCompare);
    }

    // ---- adding

    [Fact]
    public async Task AddingAnActivePropertyPutsItInTheFirstPositionAndCommitsOnce()
    {
        var propertyId = SeedProperty();

        var result = await AddAsync(propertyId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Created);
        var item = Assert.Single(_comparison.Added);
        Assert.Equal(1, item.Position);
        Assert.Equal(Now, item.AddedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(propertyId, Assert.Single(result.Value.Comparison.Items).Property.Id);
        Assert.False(result.Value.Comparison.CanCompare);
    }

    [Fact]
    public async Task TwoPropertiesCanBeComparedAndThreeFillTheList()
    {
        var first = await AddAsync(SeedProperty());
        var second = await AddAsync(SeedProperty());
        var third = await AddAsync(SeedProperty());

        Assert.False(first.Value.Comparison.CanCompare);
        Assert.True(second.Value.Comparison.CanCompare);
        Assert.True(third.Value.Comparison.CanCompare);
        Assert.Equal([1, 2, 3], _comparison.Stored.Select(item => item.Position));
        Assert.Equal(3, third.Value.Comparison.MaxItems);
    }

    [Fact]
    public async Task AFourthPropertyIsAConflict()
    {
        for (var index = 0; index < 3; index++)
        {
            await AddAsync(SeedProperty());
        }

        var result = await AddAsync(SeedProperty());

        Assert.Equal("comparison.limit_reached", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(3, _comparison.Stored.Count);
    }

    [Fact]
    public async Task AnyActivePropertyCanBeComparedWhateverItsAdvertisement()
    {
        // A comparison holds physical properties. Sale or rent is a listing fact and neither gates nor splits the list.
        var first = await AddAsync(SeedProperty());
        var second = await AddAsync(SeedProperty());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, _comparison.Stored.Count);
    }


    [Fact]
    public async Task AddingTheSamePropertyAgainChangesNothing()
    {
        var propertyId = SeedProperty();
        await AddAsync(propertyId);

        var again = await AddAsync(propertyId);

        Assert.True(again.IsSuccess);
        Assert.False(again.Value.Created);
        Assert.Single(_comparison.Stored);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AFullListStillAnswersAPropertyAlreadyOnItWithoutAConflict()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            ids.Add(SeedProperty());
            await AddAsync(ids[^1]);
        }

        var again = await AddAsync(ids[0]);

        Assert.True(again.IsSuccess);
        Assert.False(again.Value.Created);
    }

    [Fact]
    public async Task AnUnknownOrArchivedPropertyIsRefused()
    {
        var archived = SeedProperty(PropertyStatus.Archived);

        var unknown = await AddAsync(Guid.NewGuid());
        var empty = await AddAsync(Guid.Empty);
        var gone = await AddAsync(archived);

        Assert.Equal(ErrorType.NotFound, unknown.Error!.Type);
        Assert.Equal(ErrorType.NotFound, empty.Error!.Type);
        Assert.Equal("comparison.property_archived", gone.Error!.Code);
        Assert.Empty(_comparison.Stored);
    }

    [Fact]
    public async Task AnUnknownUserIsRefused()
    {
        var handler = AddHandler();

        var result = await handler.Handle(new AddToComparisonCommand(Guid.NewGuid(), SeedProperty()));

        Assert.Equal("users.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task ARemovedPositionIsReusedBeforeTheNextOne()
    {
        var first = SeedProperty();
        await AddAsync(first);
        await AddAsync(SeedProperty());
        await new RemoveFromComparisonCommandHandler(_comparison, _unitOfWork)
            .Handle(new RemoveFromComparisonCommand(_userId, first));

        await AddAsync(SeedProperty());

        Assert.Equal([1, 2], _comparison.Stored.Select(item => item.Position).Order());
    }

    // ---- concurrent adds

    [Fact]
    public async Task LosingTheRaceForThePositionAsksTheCallerToRetry()
    {
        _unitOfWork.ExceptionToThrow = new UniqueConstraintViolationException(
            PersistenceConstraint.PropertyComparisonPosition,
            new InvalidOperationException("Simulated concurrent add."));

        var result = await AddAsync(SeedProperty());

        Assert.Equal("comparison.concurrent_change", result.Error!.Code);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    [Fact]
    public async Task LosingTheRaceForTheSamePropertyStillSucceeds()
    {
        _unitOfWork.ExceptionToThrow = new UniqueConstraintViolationException(
            PersistenceConstraint.PropertyComparisonItem,
            new InvalidOperationException("Simulated concurrent add."));

        var result = await AddAsync(SeedProperty());

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
    }

    // ---- removing, clearing, reading

    [Fact]
    public async Task RemovingTakesItOffAndRemovingAgainIsFine()
    {
        var propertyId = SeedProperty();
        await AddAsync(propertyId);
        var handler = new RemoveFromComparisonCommandHandler(_comparison, _unitOfWork);

        var removed = await handler.Handle(new RemoveFromComparisonCommand(_userId, propertyId));
        var again = await handler.Handle(new RemoveFromComparisonCommand(_userId, propertyId));
        var unknown = await handler.Handle(new RemoveFromComparisonCommand(_userId, Guid.NewGuid()));

        Assert.Empty(removed.Value.Items);
        Assert.True(again.IsSuccess);
        Assert.True(unknown.IsSuccess);
        Assert.Equal(2, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ClearingEmptiesTheListOfThisUserOnly()
    {
        var other = Guid.NewGuid();
        var mine = SeedProperty();
        await AddAsync(mine);
        _comparison.Stored.Add(new PropertyComparisonItem(other, mine, 1, Now));

        var result = await new ClearComparisonCommandHandler(_comparison, _unitOfWork)
            .Handle(new ClearComparisonCommand(_userId));

        Assert.Empty(result.Value.Items);
        Assert.Equal(other, Assert.Single(_comparison.Stored).UserId);
    }

    [Fact]
    public async Task TheListCarriesTheMatchWhenThereIsOneAndNullWhenThereIsNot()
    {
        var matched = SeedProperty();
        var unmatched = SeedProperty();
        _comparison.Matches[matched] = 94m;
        await AddAsync(matched);
        await AddAsync(unmatched);

        var result = await new GetComparisonQueryHandler(_comparison).Handle(new GetComparisonQuery(_userId));

        Assert.Equal(94m, result.Value.Items.Single(item => item.Property.Id == matched).MatchPercentage);
        Assert.Null(result.Value.Items.Single(item => item.Property.Id == unmatched).MatchPercentage);
    }

    [Fact]
    public async Task AnEmptyListIsAnEmptyListNotAnError()
    {
        var result = await new GetComparisonQueryHandler(_comparison).Handle(new GetComparisonQuery(_userId));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Items);
        Assert.False(result.Value.CanCompare);
    }

    [Fact]
    public async Task ASignedInUserIsRequiredEverywhere()
    {
        var add = await AddHandler().Handle(new AddToComparisonCommand(Guid.Empty, Guid.NewGuid()));
        var remove = await new RemoveFromComparisonCommandHandler(_comparison, _unitOfWork)
            .Handle(new RemoveFromComparisonCommand(Guid.Empty, Guid.NewGuid()));
        var clear = await new ClearComparisonCommandHandler(_comparison, _unitOfWork)
            .Handle(new ClearComparisonCommand(Guid.Empty));
        var get = await new GetComparisonQueryHandler(_comparison).Handle(new GetComparisonQuery(Guid.Empty));

        Assert.All(
            new[] { add.Error, remove.Error, clear.Error, get.Error },
            error => Assert.Equal(ErrorType.Validation, error!.Type));
    }

    // ---- helpers

    private AddToComparisonCommandHandler AddHandler()
    {
        return new AddToComparisonCommandHandler(_users, _properties, _comparison, _clock, _unitOfWork);
    }

    private Task<Result<AddToComparisonResult>> AddAsync(Guid propertyId)
    {
        return AddHandler().Handle(new AddToComparisonCommand(_userId, propertyId));
    }

    private Guid SeedProperty(PropertyStatus status = PropertyStatus.Active)
    {
        var id = Guid.NewGuid();

        _properties.Details[id] = new PropertyDetails(
            id,
            PropertyType.Villa,
            status,
            new PropertyAddressDetails("SA", "Riyadh", null, "Al Malqa", null, null, null, null),
            Now.AddDays(-30),
            Now.AddDays(-30),
            Physical: new PropertyPhysicalDetails(120m, 3, true));
        // The comparison reads the same property read model through its own repository.
        _comparison.Details[id] = _properties.Details[id];

        return id;
    }
}
