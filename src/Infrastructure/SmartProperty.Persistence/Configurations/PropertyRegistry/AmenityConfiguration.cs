using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyRegistry;

internal sealed class AmenityConfiguration : IEntityTypeConfiguration<Amenity>
{
    public void Configure(EntityTypeBuilder<Amenity> builder)
    {
        builder.ToTable("amenities", "registry");

        builder.HasKey(amenity => amenity.Id);

        builder.Property(amenity => amenity.Id)
            .HasColumnName("id");

        builder.Property(amenity => amenity.Code)
            .HasColumnName("code")
            .HasMaxLength(Amenity.MaxCodeLength)
            .IsRequired();

        builder.Property(amenity => amenity.NameAr)
            .HasColumnName("name_ar")
            .HasMaxLength(Amenity.MaxNameLength)
            .IsRequired();

        builder.Property(amenity => amenity.NameEn)
            .HasColumnName("name_en")
            .HasMaxLength(Amenity.MaxNameLength)
            .IsRequired();

        builder.Property(amenity => amenity.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();

        builder.Property(amenity => amenity.AiCode)
            .HasColumnName("ai_code")
            .HasMaxLength(Amenity.MaxCodeLength);

        builder.HasIndex(amenity => amenity.Code)
            .IsUnique()
            .HasDatabaseName("ux_registry_amenities_code");
    }
}
