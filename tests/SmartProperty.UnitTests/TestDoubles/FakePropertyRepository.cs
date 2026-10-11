using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePropertyRepository : IPropertyRepository
{
    public List<Property> Added { get; } = [];

    public int AddCallCount { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    /// <summary>What <see cref="GetDetailsByIdAsync"/> answers, keyed by id. Missing ids answer null.</summary>
    public Dictionary<Guid, PropertyDetails> Details { get; } = [];

    public int GetDetailsByIdCallCount { get; private set; }

    public Guid? LastRequestedId { get; private set; }

    /// <summary>
    /// The aggregates <see cref="GetByIdAsync"/> hands out, keyed by id. Returned by reference, as a tracking
    /// context would, so a test observes exactly what the handler mutated.
    /// </summary>
    public Dictionary<Guid, Property> Tracked { get; } = [];

    public int GetByIdCallCount { get; private set; }

    public Task<Property?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        CancellationToken = cancellationToken;
        return Task.FromResult(Tracked.GetValueOrDefault(id));
    }

    /// <summary>What <see cref="ListAsync"/> answers, returned as-is so a test can prove ordering is preserved.</summary>
    public PropertyPage Page { get; set; } = new([], 0);

    public int ListCallCount { get; private set; }

    public ListCall? LastListCall { get; private set; }

    public Task AddAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        AddCallCount++;
        CancellationToken = cancellationToken;
        Added.Add(property);
        return Task.CompletedTask;
    }

    public Task<PropertyDetails?> GetDetailsByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetDetailsByIdCallCount++;
        LastRequestedId = id;
        CancellationToken = cancellationToken;
        return Task.FromResult(Details.GetValueOrDefault(id));
    }

    public Task<PropertyPage> ListAsync(
        PropertyType? type,
        PropertyStatus? status,
        string? countryCode,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;
        LastListCall = new ListCall(type, status, countryCode, skip, take);
        CancellationToken = cancellationToken;
        return Task.FromResult(Page);
    }

    internal sealed record ListCall(
        PropertyType? Type,
        PropertyStatus? Status,
        string? CountryCode,
        int Skip,
        int Take);
}
