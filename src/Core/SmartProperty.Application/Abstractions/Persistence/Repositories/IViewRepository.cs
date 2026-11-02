using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IViewRepository
{
    /// <summary>
    /// The tracked views whose code is in <paramref name="codes"/>, for attaching to a property. Codes that match
    /// nothing are simply absent from the result; the caller compares to find them.
    /// </summary>
    Task<IReadOnlyList<PropertyView>> GetByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default);

    /// <summary>Every view as a read model, in display order.</summary>
    Task<IReadOnlyList<ViewDetails>> ListAsync(CancellationToken cancellationToken = default);
}
