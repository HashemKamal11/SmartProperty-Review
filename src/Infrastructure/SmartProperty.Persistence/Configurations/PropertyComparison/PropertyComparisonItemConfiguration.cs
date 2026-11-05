using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyComparison;

internal sealed class PropertyComparisonItemConfiguration : IEntityTypeConfiguration<PropertyComparisonItem>
{
    /// <summary>The primary key, which is what makes a second add of the same property the same item.</summary>
    public const string PrimaryKeyName = "PK_property_comparison_items";

    /// <summary>One item per position in a user's list: with positions 1 to 3, this is what caps the list at three.</summary>
    public const string PositionUniqueIndexName = "ux_registry_property_comparison_items_user_position";

    public void Configure(EntityTypeBuilder<PropertyComparisonItem> builder)
    {
        builder.ToTable("property_comparison_items", "registry", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_registry_property_comparison_items_position",
                $"position BETWEEN 1 AND {PropertyComparisonItem.MaxItems}");
        });

        builder.HasKey(item => new { item.UserId, item.PropertyId })
            .HasName(PrimaryKeyName);

        builder.Property(item => item.UserId)
            .HasColumnName("user_id");

        builder.Property(item => item.PropertyId)
            .HasColumnName("property_id");

        builder.Property(item => item.Position)
            .HasColumnName("position")
            .IsRequired();

        builder.Property(item => item.AddedAt)
            .HasColumnName("added_at")
            .IsRequired();

        // Neither end is ever deleted (users are deactivated, properties archived), so nothing cascades.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(item => item.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => new { item.UserId, item.Position })
            .IsUnique()
            .HasDatabaseName(PositionUniqueIndexName);

        builder.HasIndex(item => item.PropertyId);
    }
}
