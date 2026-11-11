using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyRegistry;

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        // The checks mirror the domain invariants so a row written outside the aggregate, such as by direct SQL,
        // cannot hold a value the domain would refuse. The country code check is a format check only; whether a
        // code is an assigned ISO 3166-1 country is not the database's concern.
        builder.ToTable("properties", "registry", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_type",
                "type IN ('Land', 'Building', 'Apartment', 'Villa', 'Duplex', 'Studio', 'Chalet', 'Townhouse', 'TwinHouse', 'Penthouse', 'StandaloneVilla', 'HotelApartment', 'Office', 'Shop', 'Warehouse', 'Farm')");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_status",
                "status IN ('Active', 'Archived')");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_finishing_status",
                "finishing_status IS NULL OR finishing_status IN ('Finished', 'LuxuryFinished', 'SemiFinished', 'CoreAndShell')");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_bedrooms_range",
                "bedrooms IS NULL OR bedrooms BETWEEN 0 AND 100");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_bathrooms_range",
                "bathrooms IS NULL OR bathrooms BETWEEN 0 AND 100");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_area_sqm_range",
                "area_sqm IS NULL OR (area_sqm > 0 AND area_sqm <= 100000)");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_floor_range",
                "floor IS NULL OR floor BETWEEN -5 AND 300");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_place",
                "compound_id IS NULL OR area_id IS NOT NULL");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_address_country_code",
                "address_country_code ~ '^[A-Z]{2}$'");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_latitude_range",
                "latitude IS NULL OR latitude BETWEEN -90 AND 90");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_longitude_range",
                "longitude IS NULL OR longitude BETWEEN -180 AND 180");
        });

        builder.HasKey(property => property.Id);

        builder.Property(property => property.Id)
            .HasColumnName("id");

        builder.Property(property => property.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(property => property.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();

        // The address is a value object with no identity of its own, so it is stored inline in this row rather
        // than in a table of its own. Coordinates keep the unconstrained numeric type so no valid domain value is
        // rounded on the way in.
        builder.ComplexProperty(property => property.Address, address =>
        {
            address.IsRequired();

            address.Property(value => value.CountryCode)
                .HasColumnName("address_country_code")
                .HasMaxLength(2)
                .IsRequired();

            address.Property(value => value.City)
                .HasColumnName("address_city");

            address.Property(value => value.Region)
                .HasColumnName("address_region");

            address.Property(value => value.District)
                .HasColumnName("address_district");

            address.Property(value => value.AddressLine)
                .HasColumnName("address_line");

            address.Property(value => value.PostalCode)
                .HasColumnName("address_postal_code");

            address.Property(value => value.Latitude)
                .HasColumnName("latitude");

            address.Property(value => value.Longitude)
                .HasColumnName("longitude");
        });

        builder.Property(property => property.Bedrooms)
            .HasColumnName("bedrooms");

        builder.Property(property => property.Bathrooms)
            .HasColumnName("bathrooms");

        builder.Property(property => property.BuiltDate)
            .HasColumnName("built_date");

        builder.Property(property => property.FinishingStatus)
            .HasColumnName("finishing_status")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasMany(property => property.Amenities)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "property_amenities",
                join => join.HasOne<Amenity>().WithMany().HasForeignKey("amenity_id").OnDelete(DeleteBehavior.Restrict),
                join => join.HasOne<Property>().WithMany().HasForeignKey("property_id").OnDelete(DeleteBehavior.Restrict),
                join =>
                {
                    join.ToTable("property_amenities", "registry");
                    join.HasKey("property_id", "amenity_id");
                });

        builder.HasMany(property => property.Views)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "property_views",
                join => join.HasOne<PropertyView>().WithMany().HasForeignKey("view_id").OnDelete(DeleteBehavior.Restrict),
                join => join.HasOne<Property>().WithMany().HasForeignKey("property_id").OnDelete(DeleteBehavior.Restrict),
                join =>
                {
                    join.ToTable("property_views", "registry");
                    join.HasKey("property_id", "view_id");
                });

        // The physical facts are a value object with no identity, stored inline like the address. Commercial and
        // advertisement data is not stored on the property: it belongs to the Marketplace listing.
        builder.ComplexProperty(property => property.Physical, physical =>
        {
            physical.IsRequired();

            physical.Property(value => value.AreaSqm)
                .HasColumnName("area_sqm")
                .HasPrecision(12, 2);

            physical.Property(value => value.Floor)
                .HasColumnName("floor");

            physical.Property(value => value.TimeZoneId)
                .HasColumnName("time_zone_id")
                .HasMaxLength(PropertyPhysicalInfo.MaxTimeZoneIdLength);

            physical.Property(value => value.Furnished)
                .HasColumnName("furnished");
        });

        // The place: an area, and optionally a compound inside it. City and country are reached through the area.
        builder.Property(property => property.AreaId)
            .HasColumnName("area_id");

        builder.HasOne(property => property.Area)
            .WithMany()
            .HasForeignKey(property => property.AreaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(property => property.CompoundId)
            .HasColumnName("compound_id");

        builder.HasOne(property => property.Compound)
            .WithMany()
            .HasForeignKey(property => property.CompoundId)
            .OnDelete(DeleteBehavior.Restrict);

        // Who registered it. Nullable: rows from before owners were recorded have none. Users are never deleted.
        builder.Property(property => property.OwnerUserId)
            .HasColumnName("owner_user_id");

        builder.HasOne<SmartProperty.Domain.Identity.User>()
            .WithMany()
            .HasForeignKey(property => property.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(property => property.OwnerUserId);

        builder.Property(property => property.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(property => property.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
