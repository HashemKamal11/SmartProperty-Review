using SmartProperty.Application.PropertyMatching.Record;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyMatching;

public sealed class RecordPropertyMatchCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePropertyRepository _properties = new();
    private readonly FakePropertyMatchRepository _matches = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _propertyId = Guid.NewGuid();

    public RecordPropertyMatchCommandHandlerTests()
    {
        _users.Seed(new User(_userId, "buyer@example.com", "Buy", "Er", Now));
        _properties.Details[_propertyId] = new PropertyDetails(
            _propertyId,
            PropertyType.Unit,
            PropertyStatus.Active,
            new PropertyAddressDetails("EG", null, null, null, null, null, null, null),
            Now,
            Now);
    }

    [Fact]
    public async Task ValidCommandStoresTheMatch()
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(_userId, _propertyId, 87.5m, "  Close to the metro.  ", "  2 bedroom flat near metro  "));

        Assert.True(result.IsSuccess);
        var match = Assert.Single(_matches.Added);
        Assert.Equal(_userId, match.UserId);
        Assert.Equal(_propertyId, match.PropertyId);
        Assert.Equal(87.5m, match.MatchPercentage);
        Assert.Equal("Close to the metro.", match.Description);
        Assert.Equal("2 bedroom flat near metro", match.Query);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Equal(match.Id, result.Value.Id);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public async Task OutOfRangePercentageIsRejected(double percentage)
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(_userId, _propertyId, (decimal)percentage, "text", "query"));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_matches.Added);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task BlankDescriptionIsRejected(string? description)
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(_userId, _propertyId, 50m, description, "query"));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task BlankQueryIsRejected(string? query)
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(_userId, _propertyId, 50m, "text", query));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Empty(_matches.Added);
    }

    [Fact]
    public async Task UnknownUserIsNotFound()
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(Guid.NewGuid(), _propertyId, 50m, "text", "query"));

        Assert.True(result.IsFailure);
        Assert.Equal("users.not_found", result.Error!.Code);
        Assert.Empty(_matches.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownPropertyIsNotFound()
    {
        var result = await CreateHandler().Handle(
            new RecordPropertyMatchCommand(_userId, Guid.NewGuid(), 50m, "text", "query"));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Empty(_matches.Added);
    }

    private RecordPropertyMatchCommandHandler CreateHandler()
    {
        return new RecordPropertyMatchCommandHandler(
            _users,
            _properties,
            _matches,
            new FakeDateTimeProvider(Now),
            _unitOfWork);
    }
}
