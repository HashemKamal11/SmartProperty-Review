using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.PropertyRegistry.Views;

/// <summary>Every view a property can have, in display order.</summary>
public sealed record GetViewsQuery : IQuery<IReadOnlyList<ViewDetails>>;
