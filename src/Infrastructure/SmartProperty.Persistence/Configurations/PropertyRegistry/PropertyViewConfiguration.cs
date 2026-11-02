using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyRegistry;

internal sealed class PropertyViewConfiguration : IEntityTypeConfiguration<PropertyView>
{
    public void Configure(EntityTypeBuilder<PropertyView> builder)
    {
        builder.ToTable("views", "registry");

        builder.HasKey(view => view.Id);

        builder.Property(view => view.Id)
            .HasColumnName("id");

        builder.Property(view => view.Code)
            .HasColumnName("code")
            .HasMaxLength(PropertyView.MaxCodeLength)
            .IsRequired();

        builder.Property(view => view.NameAr)
            .HasColumnName("name_ar")
            .HasMaxLength(PropertyView.MaxNameLength)
            .IsRequired();

        builder.Property(view => view.NameEn)
            .HasColumnName("name_en")
            .HasMaxLength(PropertyView.MaxNameLength)
            .IsRequired();

        builder.Property(view => view.SortOrder)
            .HasColumnName("sort_order")
            .IsRequired();

        builder.HasIndex(view => view.Code)
            .IsUnique()
            .HasDatabaseName("ux_registry_views_code");
    }
}
