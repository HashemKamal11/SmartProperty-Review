using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.GetById;

/// <summary>Reads one canonical property, whatever its status.</summary>
public sealed record GetPropertyByIdQuery(Guid Id) : IQuery<PropertyDetails>;
