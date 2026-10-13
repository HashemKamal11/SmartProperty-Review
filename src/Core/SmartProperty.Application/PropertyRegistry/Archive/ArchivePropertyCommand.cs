using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Archive;

/// <summary>
/// Retires one active property. The record is kept: archiving changes its status, never deletes it.
/// </summary>
public sealed record ArchivePropertyCommand(Guid Id) : ICommand<PropertyDetails>;
