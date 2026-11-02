using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Application.PropertyRegistry.Ai.Sync;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;
using static SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class SyncPropertyWithAiCommandHandlerTests
{
    private readonly FakePropertyRepository _properties = new();
    private readonly FakePropertyAiSynchronizer _synchronizer = new();

    [Fact]
    public async Task AnActivePropertyIsSentAndReturnedWithItsSyncState()
    {
        var property = Complete();
        _properties.Tracked.Add(property.Id, property);

        var result = await CreateHandler().Handle(new SyncPropertyWithAiCommand(property.Id));

        Assert.True(result.IsSuccess);
        Assert.Same(property, Assert.Single(_synchronizer.Synced));
        Assert.Equal(property.Id, result.Value.Id);
        Assert.Equal(AiSyncStatus.NotSynced, result.Value.AiSync!.Status);
    }

    [Fact]
    public async Task AnUnknownPropertyIsNotFound()
    {
        var result = await CreateHandler().Handle(new SyncPropertyWithAiCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Empty(_synchronizer.Synced);
    }

    [Fact]
    public async Task TheEmptyIdIsNotFoundWithoutAskingTheRepository()
    {
        var result = await CreateHandler().Handle(new SyncPropertyWithAiCommand(Guid.Empty));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.not_found", result.Error!.Code);
        Assert.Equal(0, _properties.GetByIdCallCount);
    }

    [Fact]
    public async Task AnArchivedPropertyIsRefusedAndNothingIsSent()
    {
        var property = Complete();
        property.Archive(CreatedAt.AddMinutes(1));
        _properties.Tracked.Add(property.Id, property);

        var result = await CreateHandler().Handle(new SyncPropertyWithAiCommand(property.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("properties.archived", result.Error!.Code);
        Assert.Empty(_synchronizer.Synced);
    }

    private SyncPropertyWithAiCommandHandler CreateHandler()
    {
        return new SyncPropertyWithAiCommandHandler(_properties, _synchronizer);
    }
}
