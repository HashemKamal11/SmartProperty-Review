using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Records what the handlers ask of the synchronizer without doing it, so a handler test is not also a test of the
/// synchronizer, and its commit counts stay about the handler.
/// </summary>
internal sealed class FakePropertyAiSynchronizer : IPropertyAiSynchronizer
{
    public List<Property> Synced { get; } = [];

    public List<Property> Removed { get; } = [];

    public Task SyncAsync(Property property, CancellationToken cancellationToken = default)
    {
        Synced.Add(property);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(Property property, CancellationToken cancellationToken = default)
    {
        Removed.Add(property);

        return Task.CompletedTask;
    }
}
