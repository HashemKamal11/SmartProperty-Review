using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.PropertyMatching;

internal sealed class PropertyMatchConfiguration : IEntityTypeConfiguration<PropertyMatch>
{
    public void Configure(EntityTypeBuilder<PropertyMatch> builder)
    {
        builder.ToTable("property_matches", "matching", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_matching_property_matches_match_percentage",
                "match_percentage BETWEEN 0 AND 100");
        });

        builder.HasKey(match => match.Id);

        builder.Property(match => match.Id)
            .HasColumnName("id");

        builder.Property(match => match.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(match => match.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(match => match.PropertyId)
            .HasColumnName("property_id")
            .IsRequired();

        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(match => match.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(match => match.MatchPercentage)
            .HasColumnName("match_percentage")
            .HasPrecision(5, 2)
            .IsRequired();

        builder.Property(match => match.Description)
            .HasColumnName("description")
            .HasMaxLength(PropertyMatch.MaxDescriptionLength)
            .IsRequired();

        builder.Property(match => match.Query)
            .HasColumnName("query")
            .HasMaxLength(PropertyMatch.MaxQueryLength)
            .IsRequired();

        builder.Property(match => match.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(match => new { match.UserId, match.CreatedAt });
        builder.HasIndex(match => match.PropertyId);
    }
}
