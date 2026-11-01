using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakeAmenityRepository : IAmenityRepository
{
    private readonly List<Amenity> _amenities = [];

    public int GetByCodesCallCount { get; private set; }

    /// <summary>Adds an amenity and returns it, so a test can attach the same instance to a property.</summary>
    public Amenity Seed(string code, int sortOrder = 0)
    {
        var amenity = new Amenity(Guid.NewGuid(), code, $"{code}-ar", $"{code}-en", sortOrder);
        _amenities.Add(amenity);

        return amenity;
    }

    public Task<IReadOnlyList<Amenity>> GetByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        GetByCodesCallCount++;

        IReadOnlyList<Amenity> found = _amenities
            .Where(amenity => codes.Contains(amenity.Code))
            .OrderBy(amenity => amenity.SortOrder)
            .ToList();

        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<AmenityDetails>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AmenityDetails> all = _amenities
            .OrderBy(amenity => amenity.SortOrder)
            .Select(amenity => new AmenityDetails(amenity.Code, amenity.NameAr, amenity.NameEn))
            .ToList();

        return Task.FromResult(all);
    }
}
