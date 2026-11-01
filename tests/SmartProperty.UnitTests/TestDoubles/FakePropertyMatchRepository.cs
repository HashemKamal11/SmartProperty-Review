using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyMatching;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePropertyMatchRepository : IPropertyMatchRepository
{
    public List<PropertyMatch> Added { get; } = [];

    public Task AddAsync(PropertyMatch propertyMatch, CancellationToken cancellationToken = default)
    {
        Added.Add(propertyMatch);

        return Task.CompletedTask;
    }
}
