using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakeViewRepository : IViewRepository
{
    private readonly List<PropertyView> _views = [];

    public int GetByCodesCallCount { get; private set; }

    /// <summary>Adds a view and returns it, so a test can attach the same instance to a property.</summary>
    public PropertyView Seed(string code, int sortOrder = 0)
    {
        var view = new PropertyView(Guid.NewGuid(), code, $"{code}-ar", $"{code}-en", sortOrder);
        _views.Add(view);

        return view;
    }

    public Task<IReadOnlyList<PropertyView>> GetByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        GetByCodesCallCount++;

        IReadOnlyList<PropertyView> found = _views
            .Where(view => codes.Contains(view.Code))
            .OrderBy(view => view.SortOrder)
            .ToList();

        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<ViewDetails>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ViewDetails> all = _views
            .OrderBy(view => view.SortOrder)
            .Select(view => new ViewDetails(view.Code, view.NameAr, view.NameEn))
            .ToList();

        return Task.FromResult(all);
    }
}
