using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Ai;

/// <summary>
/// Keeps the AI search service's copy of a property in step with the registry, and records the outcome on the
/// property.
/// </summary>
/// <remarks>
/// Best effort by design: the property is already saved when these run, so an outage of the AI service never
/// loses or rejects a registry write. It is recorded on the property instead (<see cref="AiSyncStatus.Failed"/>
/// with the reason) and a later sync repairs it. Neither method throws for a failed call.
/// </remarks>
public interface IPropertyAiSynchronizer
{
    /// <summary>
    /// Sends the property, or records why it cannot be sent yet. Commits the outcome when it changed.
    /// </summary>
    Task SyncAsync(Property property, CancellationToken cancellationToken = default);

    /// <summary>Removes the property from search, as when it is archived. Commits the outcome.</summary>
    Task RemoveAsync(Property property, CancellationToken cancellationToken = default);
}
