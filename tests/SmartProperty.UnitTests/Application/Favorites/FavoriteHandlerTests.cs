using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Favorites.Add;
using SmartProperty.Application.Favorites.List;
using SmartProperty.Application.Favorites.Remove;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.Favorites;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Favorites;

public sealed class FavoriteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePropertyRepository _properties = new();
    private readonly FakeFavoritePropertyRepository _favorites = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Guid _userId = Guid.NewGuid();

    public FavoriteHandlerTests()
    {
        _users.Seed(new User(_userId, "buyer@example.com", "Buy", "Er", Now));
    }

    // ---- add

    [Fact]
    public async Task AddingAnActivePropertyCreatesTheFavoriteAndCommitsOnce()
    {
        var propertyId = SeedProperty();

        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Created);
        Assert.Equal(propertyId, result.Value.PropertyId);
        Assert.Equal(Now, result.Value.FavoritedAt);
        var favorite = Assert.Single(_favorites.Added);
        Assert.Equal(_userId, favorite.UserId);
        Assert.Equal(propertyId, favorite.PropertyId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AddingAPropertyThatIsAlreadyAFavoriteChangesNothingAndKeepsTheOriginalInstant()
    {
        var propertyId = SeedProperty();
        var earlier = Now.AddDays(-3);
        _favorites.Stored.Add(new FavoriteProperty(_userId, propertyId, earlier));

        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(earlier, result.Value.FavoritedAt);
        Assert.Empty(_favorites.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AFavoriteThatIsAlreadyThereSurvivesTheArchivingOfItsProperty()
    {
        var propertyId = SeedProperty(PropertyStatus.Archived);
        _favorites.Stored.Add(new FavoriteProperty(_userId, propertyId, Now.AddDays(-1)));

        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
    }

    [Fact]
    public async Task AnUnknownPropertyIsNotFound()
    {
        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Empty(_favorites.Added);
    }

    [Fact]
    public async Task TheEmptyPropertyIdIsNotFoundWithoutAskingTheRepository()
    {
        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(0, _properties.GetDetailsByIdCallCount);
    }

    [Fact]
    public async Task AnArchivedPropertyCannotBeAdded()
    {
        var propertyId = SeedProperty(PropertyStatus.Archived);

        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsFailure);
        Assert.Equal("favorites.property_archived", result.Error!.Code);
        Assert.Empty(_favorites.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AnUnknownUserIsRefusedBeforeAnythingIsAdded()
    {
        var propertyId = SeedProperty();

        var result = await AddHandler().Handle(new AddFavoriteCommand(Guid.NewGuid(), propertyId));

        Assert.True(result.IsFailure);
        Assert.Equal("users.not_found", result.Error!.Code);
        Assert.Empty(_favorites.Added);
    }

    [Fact]
    public async Task TheEmptyUserIdIsRejected()
    {
        var result = await AddHandler().Handle(new AddFavoriteCommand(Guid.Empty, SeedProperty()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task ARivalThatMarkedTheSamePropertyFirstIsNotAnError()
    {
        var propertyId = SeedProperty();
        var rival = new FavoriteProperty(_userId, propertyId, Now.AddSeconds(-1));
        _favorites.HideFavoriteOnFirstFind = true;
        _unitOfWork.ExceptionToThrow = new UniqueConstraintViolationException(
            PersistenceConstraint.FavoriteProperty,
            new InvalidOperationException("duplicate key"));
        _unitOfWork.DiscardTrackedChangesAction = () =>
        {
            _favorites.Stored.Clear();
            _favorites.Stored.Add(rival);
        };

        var result = await AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(rival.CreatedAt, result.Value.FavoritedAt);
        Assert.Equal(1, _unitOfWork.DiscardTrackedChangesCallCount);
    }

    [Fact]
    public async Task AnyOtherUniqueViolationStillPropagates()
    {
        var propertyId = SeedProperty();
        _unitOfWork.ExceptionToThrow = new UniqueConstraintViolationException(
            PersistenceConstraint.UserEmail,
            new InvalidOperationException("duplicate key"));

        await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => AddHandler().Handle(new AddFavoriteCommand(_userId, propertyId)));
    }

    // ---- remove

    [Fact]
    public async Task RemovingAFavoriteDeletesItAndCommits()
    {
        var propertyId = SeedProperty();
        _favorites.Stored.Add(new FavoriteProperty(_userId, propertyId, Now));

        var result = await RemoveHandler().Handle(new RemoveFavoriteCommand(_userId, propertyId));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Removed);
        Assert.Empty(_favorites.Stored);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotAFavoriteIsASuccessThatCommitsNothing()
    {
        var result = await RemoveHandler().Handle(new RemoveFavoriteCommand(_userId, Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Removed);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RemovingNeverTouchesAnotherUsersFavorite()
    {
        var propertyId = SeedProperty();
        var other = new FavoriteProperty(Guid.NewGuid(), propertyId, Now);
        _favorites.Stored.Add(other);

        var result = await RemoveHandler().Handle(new RemoveFavoriteCommand(_userId, propertyId));

        Assert.False(result.Value.Removed);
        Assert.Same(other, Assert.Single(_favorites.Stored));
    }

    [Fact]
    public async Task RemovingTheEmptyPropertyIdIsASuccessWithoutAskingTheRepository()
    {
        var result = await RemoveHandler().Handle(new RemoveFavoriteCommand(_userId, Guid.Empty));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Removed);
        Assert.Equal(0, _favorites.RemoveCallCount);
    }

    [Fact]
    public async Task RemovingForTheEmptyUserIsRejected()
    {
        var result = await RemoveHandler().Handle(new RemoveFavoriteCommand(Guid.Empty, Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    // ---- list

    [Fact]
    public async Task TheListIsTheUsersOwnNewestFirstWithItsPropertyDetails()
    {
        var older = SeedProperty();
        var newer = SeedProperty();
        _favorites.Stored.Add(new FavoriteProperty(_userId, older, Now.AddDays(-2)));
        _favorites.Stored.Add(new FavoriteProperty(_userId, newer, Now.AddDays(-1)));
        _favorites.Stored.Add(new FavoriteProperty(Guid.NewGuid(), older, Now));

        var result = await new GetFavoritesQueryHandler(_favorites).Handle(new GetFavoritesQuery(_userId, null, null));

        Assert.True(result.IsSuccess);
        Assert.Equal([newer, older], result.Value.Items.Select(item => item.Property.Id));
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal(Now.AddDays(-1), result.Value.Items[0].FavoritedAt);
    }

    [Theory]
    [InlineData(null, null, 0, 10)]
    [InlineData(1, 5, 0, 5)]
    [InlineData(3, 5, 10, 5)]
    [InlineData(0, 0, 0, 1)]
    public async Task PagingIsDefaultedAndClampedLikeEveryOtherList(int? page, int? pageSize, int skip, int take)
    {
        await new GetFavoritesQueryHandler(_favorites).Handle(new GetFavoritesQuery(_userId, page, pageSize));

        Assert.Equal((_userId, skip, take), _favorites.LastList);
    }

    [Fact]
    public async Task TheListForTheEmptyUserIsRejected()
    {
        var result = await new GetFavoritesQueryHandler(_favorites).Handle(new GetFavoritesQuery(Guid.Empty, null, null));

        Assert.True(result.IsFailure);
        Assert.Null(_favorites.LastList);
    }

    [Fact]
    public async Task TheIdListIsTheUsersOwnNewestFirst()
    {
        var first = SeedProperty();
        var second = SeedProperty();
        _favorites.Stored.Add(new FavoriteProperty(_userId, first, Now.AddDays(-2)));
        _favorites.Stored.Add(new FavoriteProperty(_userId, second, Now.AddDays(-1)));
        _favorites.Stored.Add(new FavoriteProperty(Guid.NewGuid(), SeedProperty(), Now));

        var result = await new GetFavoriteIdsQueryHandler(_favorites).Handle(new GetFavoriteIdsQuery(_userId));

        Assert.Equal([second, first], result.Value);
    }

    [Fact]
    public async Task TheIdListForTheEmptyUserIsRejected()
    {
        var result = await new GetFavoriteIdsQueryHandler(_favorites).Handle(new GetFavoriteIdsQuery(Guid.Empty));

        Assert.True(result.IsFailure);
    }

    private Guid SeedProperty(PropertyStatus status = PropertyStatus.Active)
    {
        var id = Guid.NewGuid();
        _properties.Details[id] = new PropertyDetails(
            id,
            PropertyType.Apartment,
            status,
            new PropertyAddressDetails("EG", null, null, null, null, null, null, null),
            Now.AddDays(-30),
            Now.AddDays(-30));
        // The favorites list reads the same property read model through its own repository.
        _favorites.Details[id] = _properties.Details[id];

        return id;
    }

    private AddFavoriteCommandHandler AddHandler()
    {
        return new AddFavoriteCommandHandler(_users, _properties, _favorites, new FakeDateTimeProvider(Now), _unitOfWork);
    }

    private RemoveFavoriteCommandHandler RemoveHandler()
    {
        return new RemoveFavoriteCommandHandler(_favorites, _unitOfWork);
    }
}
