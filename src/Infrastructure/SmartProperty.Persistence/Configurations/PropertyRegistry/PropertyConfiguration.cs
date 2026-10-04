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
                "type IN ('Land', 'Building', 'Unit')");

            tableBuilder.HasCheckConstraint(
                "ck_registry_properties_status",
                "status IN ('Active', 'Archived')");

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
            .IsRequired();

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

        builder.Property(property => property.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(property => property.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
