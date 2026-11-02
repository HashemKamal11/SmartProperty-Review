using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyImages;

internal sealed class PropertyImageConfiguration : IEntityTypeConfiguration<PropertyImage>
{
    public void Configure(EntityTypeBuilder<PropertyImage> builder)
    {
        // The checks mirror the domain rules so a row written outside the aggregate, such as by direct SQL, cannot hold
        // an image type the platform would refuse to serve.
        builder.ToTable("property_images", "registry", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_registry_property_images_content_type",
                "content_type IN ('image/jpeg', 'image/png', 'image/webp')");

            tableBuilder.HasCheckConstraint(
                "ck_registry_property_images_size",
                "size_bytes > 0 AND size_bytes <= 5242880");

            tableBuilder.HasCheckConstraint(
                "ck_registry_property_images_sort_order",
                "sort_order >= 0");
        });

        builder.HasKey(image => image.Id);

        builder.Property(image => image.Id)
            .HasColumnName("id");

        builder.Property(image => image.PropertyId)
            .HasColumnName("property_id")
            .IsRequired();

        builder.Property(image => image.StorageKey)
            .HasColumnName("storage_key")
            .HasMaxLength(PropertyImage.MaxStorageKeyLength)
            .IsRequired();

        builder.Property(image => image.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(PropertyImage.MaxFileNameLength)
            .IsRequired();

        builder.Property(image => image.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(image => image.SizeBytes)
            .HasColumnName("size_bytes")
            .IsRequired();

        builder.Property(image => image.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();

        builder.Property(image => image.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // A property is never deleted (it is archived), so its photos are never cascaded away.
        builder.HasOne<Property>()
            .WithMany(property => property.Images)
            .HasForeignKey(image => image.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        // No unique (property, order): reordering moves several rows, and a non-deferrable unique index would refuse the
        // intermediate state. The use case writes a complete 0..n-1 order, and readers sort by it.
        builder.HasIndex(image => new { image.PropertyId, image.SortOrder });

        builder.HasIndex(image => image.StorageKey)
            .IsUnique()
            .HasDatabaseName("ux_registry_property_images_storage_key");
    }
}
