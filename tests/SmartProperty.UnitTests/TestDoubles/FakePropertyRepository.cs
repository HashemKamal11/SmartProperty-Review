using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePropertyRepository : IPropertyRepository
{
    public List<Property> Added { get; } = [];

    public int AddCallCount { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public Task AddAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        AddCallCount++;
        CancellationToken = cancellationToken;
        Added.Add(property);
        return Task.CompletedTask;
    }
}
