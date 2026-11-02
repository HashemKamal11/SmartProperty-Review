using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Favorites;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Favorites;

internal sealed class FavoritePropertyConfiguration : IEntityTypeConfiguration<FavoriteProperty>
{
    /// <summary>The primary key, which is what makes a user's second mark of a property the same favorite.</summary>
    public const string PrimaryKeyName = "PK_favorite_properties";

    public void Configure(EntityTypeBuilder<FavoriteProperty> builder)
    {
        builder.ToTable("favorite_properties", "registry");

        builder.HasKey(favorite => new { favorite.UserId, favorite.PropertyId })
            .HasName(PrimaryKeyName);

        builder.Property(favorite => favorite.UserId)
            .HasColumnName("user_id");

        builder.Property(favorite => favorite.PropertyId)
            .HasColumnName("property_id");

        builder.Property(favorite => favorite.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Neither end is ever deleted (users are deactivated, properties archived), so nothing cascades: a delete
        // that would orphan a favorite is refused rather than silently removing a user's list.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(favorite => favorite.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(favorite => favorite.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        // A user's list is read newest first.
        builder.HasIndex(favorite => new { favorite.UserId, favorite.CreatedAt });
        builder.HasIndex(favorite => favorite.PropertyId);
    }
}
