using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Share;

/// <summary>
/// The property behind a share link. Only an Active property can be shared: an archived or unknown one is not found,
/// so a stale link never advertises a property that is no longer offered.
/// </summary>
public sealed record GetPropertyShareQuery(Guid Id) : IQuery<PropertyDetails>;
