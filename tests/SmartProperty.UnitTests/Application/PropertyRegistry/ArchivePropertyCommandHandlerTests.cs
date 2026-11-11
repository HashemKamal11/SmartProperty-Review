using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.PropertyRegistry.Archive;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class ArchivePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ArchivedAt = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly FakePropertyRepository _properties = new();
    private readonly FakeDateTimeProvider _clock = new(ArchivedAt);
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task ActivePropertyIsArchivedAndCommittedOnce()
    {
        var address = new PropertyAddress("AE", "Dubai", latitude: 25.2m);
        var property = SeedProperty(address);

        var result = await CreateHandler().Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Equal(ArchivedAt, property.UpdatedAt);
        Assert.Equal(PropertyType.Land, property.Type);
        Assert.Same(address, property.Address);
        Assert.Equal(CreatedAt, property.CreatedAt);

        Assert.Equal(1, _clock.UtcNowCallCount);
        Assert.Equal(1, _properties.GetByIdCallCount);
        Assert.Equal(property.Id, _properties.LastRequestedId);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _properties.AddCallCount);

        var details = result.Value;
        Assert.Equal(property.Id, details.Id);
        Assert.Equal(PropertyType.Land, details.Type);
        Assert.Equal(PropertyStatus.Archived, details.Status);
        Assert.Equal("AE", details.Address.CountryCode);
        Assert.Equal("Dubai", details.Address.City);
        Assert.Null(details.Address.Region);
        Assert.Null(details.Address.District);
        Assert.Null(details.Address.AddressLine);
        Assert.Null(details.Address.PostalCode);
        Assert.Equal(25.2m, details.Address.Latitude);
        Assert.Null(details.Address.Longitude);
        Assert.Equal(CreatedAt, details.CreatedAt);
        Assert.Equal(ArchivedAt, details.UpdatedAt);
    }

    [Fact]
    public async Task AlreadyArchivedPropertyReturnsConflictWithoutSavingOrReadingTheClock()
    {
        var property = SeedProperty();
        var archivedAt = CreatedAt.AddDays(1);
        property.Archive(archivedAt);

        var result = await CreateHandler().Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Equal("The property is already archived.", result.Error.Description);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Equal(archivedAt, property.UpdatedAt);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task UnknownIdReturnsNotFoundWithoutSavingOrReadingTheClock()
    {
        var result = await CreateHandler().Handle(new ArchivePropertyCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
        Assert.Equal(1, _properties.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task EmptyIdReturnsNotFoundWithoutTouchingTheRepository()
    {
        var result = await CreateHandler().Handle(new ArchivePropertyCommand(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(0, _properties.GetByIdCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(0, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task ClockBehindKeepsTheCurrentUpdateInstant()
    {
        var property = SeedProperty();
        _clock.UtcNow = CreatedAt.AddHours(-1);

        var result = await CreateHandler().Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Equal(CreatedAt, property.UpdatedAt);
        Assert.Equal(CreatedAt, result.Value.UpdatedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task ArchiveTruncatesTheClockToMicrosecondPrecision()
    {
        var property = SeedProperty();
        _clock.UtcNow = ArchivedAt.AddTicks(15);

        var result = await CreateHandler().Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(ArchivedAt.AddTicks(10), property.UpdatedAt);
        Assert.Equal(ArchivedAt.AddTicks(10), result.Value.UpdatedAt);
        Assert.Equal(1, _clock.UtcNowCallCount);
    }

    [Fact]
    public async Task ConcurrentStatusChangeReturnsArchivedConflict()
    {
        var property = SeedProperty();
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.PropertyConcurrencyConflict();

        var result = await CreateHandler().Handle(new ArchivePropertyCommand(property.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ConflictOnAnotherResourceIsNotTurnedIntoAnArchivedConflict()
    {
        var property = SeedProperty();
        var conflict = FakeUnitOfWork.RefreshTokenConcurrencyConflict();
        _unitOfWork.ExceptionToThrow = conflict;

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => CreateHandler().Handle(new ArchivePropertyCommand(property.Id)));

        Assert.Same(conflict, thrown);
        Assert.Equal(PersistenceResource.RefreshToken, thrown.Resource);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task CancellationTokenReachesTheRepository()
    {
        var property = SeedProperty();
        using var cancellation = new CancellationTokenSource();

        await CreateHandler().Handle(new ArchivePropertyCommand(property.Id), cancellation.Token);

        Assert.Equal(cancellation.Token, _properties.CancellationToken);
    }

    private Property SeedProperty(PropertyAddress? address = null)
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address ?? new PropertyAddress("SA"), CreatedAt);
        _properties.Tracked.Add(property.Id, property);
        return property;
    }

    private ArchivePropertyCommandHandler CreateHandler()
    {
        return new ArchivePropertyCommandHandler(_properties, _clock, _unitOfWork);
    }
}
