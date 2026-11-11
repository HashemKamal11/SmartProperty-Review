using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

/// <summary>Complete and bare properties shared by the property tests.</summary>
internal static class AiTestProperties
{
    public static readonly DateTimeOffset CreatedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    public static readonly Amenity Pool = new(Guid.NewGuid(), "SwimmingPool", "مسبح", "Swimming pool", 6, "pool");
    public static readonly Amenity PrivatePool = new(Guid.NewGuid(), "PrivatePool", "مسبح خاص", "Private pool", 10, "pool");
    public static readonly Amenity SharedGym = new(Guid.NewGuid(), "SharedGym", "نادي رياضي عام", "Shared gym", 15, "gym");
    public static readonly Amenity Balcony = new(Guid.NewGuid(), "Balcony", "شرفة", "Balcony", 1, "balcony");
    public static readonly Amenity Barbecue = new(Guid.NewGuid(), "BarbecueArea", "منطقة شواء", "Barbecue area", 20);

    public static readonly PropertyView City = new(Guid.NewGuid(), "City", "مدينة", "City", 1);
    public static readonly PropertyView Sea = new(Guid.NewGuid(), "Sea", "بحر", "Sea", 7);

    /// <summary>The place tree the complete property sits in: Egypt, Cairo, Helwan (AI id 12), and a compound (AI id 7).</summary>
    public static readonly Country Egypt = new(Guid.NewGuid(), 1, "Egypt", "مصر");
    public static readonly SmartProperty.Domain.Locations.City Cairo = new(Guid.NewGuid(), 3, Egypt, "Cairo", "القاهرة");
    public static readonly Area Helwan = new(Guid.NewGuid(), 12, Cairo, "Helwan", "حلوان");
    public static readonly Compound GardenResidence = new(Guid.NewGuid(), 7, Helwan, "Garden Residence", "جاردن ريزيدنس");
    public static readonly Area Maadi = new(Guid.NewGuid(), 13, Cairo, "Maadi", "المعادي");

    /// <summary>A property with every physical fact and a place.</summary>
    public static Property Complete(
        IEnumerable<Amenity>? amenities = null,
        IEnumerable<PropertyView>? views = null,
        FinishingStatus? finishing = FinishingStatus.LuxuryFinished,
        bool inCompound = true)
    {
        return new Property(
            Guid.NewGuid(),
            PropertyType.TwinHouse,
            new PropertyAddress("EG", "Cairo", region: null, district: "Helwan"),
            CreatedAt,
            bedrooms: 4,
            bathrooms: 3,
            finishingStatus: finishing,
            amenities: amenities,
            views: views,
            physical: new PropertyPhysicalInfo(210.5m, floor: 2, furnished: true),
            area: Helwan,
            compound: inCompound ? GardenResidence : null);
    }

    /// <summary>A property with nothing but its type and country.</summary>
    public static Property Bare()
    {
        return new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt);
    }
}
