using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Ai.Sync;

/// <summary>
/// Sends one property to the AI search service again: the repair for a property whose last attempt failed, or one
/// that was incomplete and has since been completed. Safe to repeat; the service replaces its copy.
/// </summary>
public sealed record SyncPropertyWithAiCommand(Guid Id) : ICommand<PropertyDetails>;
