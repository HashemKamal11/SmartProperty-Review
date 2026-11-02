using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;
using static SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class PropertyAiSynchronizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeAiSearchGateway _gateway = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task ACompletePropertyIsSentUnderItsOwnIdAndRecordedAsSynced()
    {
        var property = Complete(amenities: [Balcony]);

        await CreateSynchronizer().SyncAsync(property);

        var (externalId, listing) = Assert.Single(_gateway.Puts);
        Assert.Equal(property.Id.ToString(), externalId);
        Assert.Equal("Twin House", listing.PropertyType);
        Assert.Equal(AiSyncStatus.Synced, property.AiSyncStatus);
        Assert.Equal(Now, property.AiSyncedAt);
        Assert.Null(property.AiSyncError);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
        Assert.Empty(_gateway.Deletes);
    }

    [Fact]
    public async Task AnIncompletePropertyIsNotSentAndSaysWhatIsMissing()
    {
        var property = Bare();

        await CreateSynchronizer().SyncAsync(property);

        Assert.Empty(_gateway.Puts);
        Assert.Empty(_gateway.Deletes);
        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Contains("missing transactionType, price, areaSqm, areaId, bedrooms, bathrooms", property.AiSyncError);
        Assert.StartsWith("Not sent to AI search", property.AiSyncError);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task APropertyThatBecameIncompleteIsTakenDownSoTheOldCopyStopsAnsweringSearches()
    {
        var property = Complete();
        property.MarkAiSynced(CreatedAt);
        property.UpdateDetails(PropertyType.TwinHouse, new PropertyAddress("EG"), CreatedAt.AddHours(1));

        await CreateSynchronizer().SyncAsync(property);

        Assert.Equal([property.Id.ToString()], _gateway.Deletes);
        Assert.Empty(_gateway.Puts);
        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Null(property.AiSyncedAt);
    }

    [Fact]
    public async Task AFailedPropertyThatBecameIncompleteIsAlsoTakenDown()
    {
        var property = Bare();
        property.MarkAiSyncFailed("earlier failure");

        await CreateSynchronizer().SyncAsync(property);

        Assert.Single(_gateway.Deletes);
        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
    }

    [Fact]
    public async Task AnUnavailableServiceIsRecordedAsFailedAndNeverThrown()
    {
        var property = Complete();
        _gateway.PutError = AiSearchErrors.Unavailable("it did not answer in time");

        await CreateSynchronizer().SyncAsync(property);

        Assert.Equal(AiSyncStatus.Failed, property.AiSyncStatus);
        Assert.Contains("did not answer in time", property.AiSyncError);
        Assert.Null(property.AiSyncedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ARejectedListingIsRecordedAsFailedWithTheReason()
    {
        var property = Complete();
        _gateway.PutError = AiSearchErrors.Rejected("area_id does not exist");

        await CreateSynchronizer().SyncAsync(property);

        Assert.Equal(AiSyncStatus.Failed, property.AiSyncStatus);
        Assert.Contains("area_id does not exist", property.AiSyncError);
    }

    [Fact]
    public async Task ADisabledIntegrationLeavesThePropertyNotSyncedRatherThanFailed()
    {
        var property = Complete();
        _gateway.PutError = AiSearchErrors.Disabled;

        await CreateSynchronizer().SyncAsync(property);

        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Contains("disabled", property.AiSyncError);
    }

    [Fact]
    public async Task AFailedPropertyIsRepairedByTheNextSuccessfulSync()
    {
        var property = Complete();
        property.MarkAiSyncFailed("earlier failure");

        await CreateSynchronizer().SyncAsync(property);

        Assert.Equal(AiSyncStatus.Synced, property.AiSyncStatus);
        Assert.Null(property.AiSyncError);
    }

    [Fact]
    public async Task ASaveThatLosesToAnArchiveIsNotAnError()
    {
        var property = Complete();
        _unitOfWork.ExceptionToThrow = new ConcurrencyConflictException(
            PersistenceResource.Property,
            new InvalidOperationException("archived meanwhile"));

        var exception = await Record.ExceptionAsync(() => CreateSynchronizer().SyncAsync(property));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AnyOtherSaveFailureStillPropagates()
    {
        var property = Complete();
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RefreshTokenConcurrencyConflict();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => CreateSynchronizer().SyncAsync(property));
    }

    [Fact]
    public async Task RemovingASyncedPropertyDeletesItAndRecordsWhy()
    {
        var property = Complete();
        property.MarkAiSynced(CreatedAt);

        await CreateSynchronizer().RemoveAsync(property);

        Assert.Equal([property.Id.ToString()], _gateway.Deletes);
        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Contains("archived", property.AiSyncError);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RemovingAPropertyThatWasNeverSentCallsNothing()
    {
        var property = Bare();

        await CreateSynchronizer().RemoveAsync(property);

        Assert.Empty(_gateway.Deletes);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AFailedRemovalIsRecordedSoItCanBeRetried()
    {
        var property = Complete();
        property.MarkAiSynced(CreatedAt);
        _gateway.DeleteError = AiSearchErrors.Unavailable("it could not be reached");

        await CreateSynchronizer().RemoveAsync(property);

        Assert.Equal(AiSyncStatus.Failed, property.AiSyncStatus);
        Assert.Contains("could not be reached", property.AiSyncError);
    }

    private PropertyAiSynchronizer CreateSynchronizer()
    {
        return new PropertyAiSynchronizer(_gateway, new FakeDateTimeProvider(Now), _unitOfWork);
    }
}
