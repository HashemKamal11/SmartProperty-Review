using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class PropertyListingConfiguration : IEntityTypeConfiguration<PropertyListing>
{
    public void Configure(EntityTypeBuilder<PropertyListing> builder)
    {
        builder.ToTable("listings", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_listings_ids",
                "id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND (published_submission_id IS NULL OR published_submission_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_marketplace_listings_state",
                "purpose IN ('Sale', 'Rent') AND ((publication_status = 'Unpublished' AND availability_status IN ('Open', 'Closed')) OR (publication_status = 'Published' AND availability_status = 'Open') OR (publication_status = 'Withdrawn' AND availability_status IN ('Reserved', 'Closed')))");
            table.HasCheckConstraint("ck_marketplace_listings_history",
                "updated_at >= created_at AND ((publication_status = 'Unpublished' AND published_submission_id IS NULL AND published_at IS NULL AND withdrawn_at IS NULL) OR (publication_status = 'Published' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND published_at >= created_at AND published_at <= updated_at AND withdrawn_at IS NULL) OR (publication_status = 'Withdrawn' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND withdrawn_at IS NOT NULL AND published_at >= created_at AND withdrawn_at >= published_at AND withdrawn_at <= updated_at))");
        });
        builder.HasKey(x => x.Id).HasName("pk_marketplace_listings");
        builder.HasAlternateKey(x => new { x.Id, x.PropertyId, x.Purpose }).HasName("ak_marketplace_listings_identity");
        builder.HasIndex(x => new { x.PropertyId, x.Purpose }).IsUnique().HasDatabaseName("ux_marketplace_listings_property_purpose");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.PropertyId).HasColumnName("property_id");
        builder.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.PublicationStatus).HasColumnName("publication_status").HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
        builder.Property(x => x.AvailabilityStatus).HasColumnName("availability_status").HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
        // Approval belongs to the later submission/review phase; a GUID alone is not approval.
        builder.Property(x => x.PublishedSubmissionId).HasColumnName("published_submission_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.WithdrawnAt).HasColumnName("withdrawn_at");
        builder.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_listings_property");
    }
}
