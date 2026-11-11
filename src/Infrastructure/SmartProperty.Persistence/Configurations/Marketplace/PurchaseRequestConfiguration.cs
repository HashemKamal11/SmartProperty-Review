using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    internal const string PurposeProperty = "ListingPurpose";

    public void Configure(EntityTypeBuilder<PurchaseRequest> builder)
    {
        builder.ToTable("purchase_requests", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_purchase_requests_ids",
                "id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND buyer_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_marketplace_purchase_requests_state",
                "listing_purpose = 'Sale' AND status IN ('Submitted', 'Accepted', 'Rejected', 'WithdrawnByRequester', 'ClosedListingUnavailable')");
            table.HasCheckConstraint("ck_marketplace_purchase_requests_terms",
                "(submission_id IS NULL AND offered_amount IS NULL AND currency IS NULL) "
                + "OR (submission_id IS NOT NULL AND offered_amount > 0 AND currency ~ '^[A-Z]{3}$')");
            table.HasCheckConstraint("ck_marketplace_purchase_requests_history",
                "updated_at >= submitted_at AND ((status = 'Submitted' AND decided_at IS NULL) OR (status <> 'Submitted' AND decided_at IS NOT NULL AND decided_at >= submitted_at AND decided_at = updated_at))");
        });
        builder.HasKey(x => x.Id).HasName("pk_marketplace_purchase_requests");
        builder.HasAlternateKey(x => new { x.Id, x.ListingId, x.PropertyId }).HasName("ak_marketplace_purchase_requests_identity");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ListingId).HasColumnName("listing_id");
        builder.Property(x => x.PropertyId).HasColumnName("property_id");
        builder.Property<ListingPurpose?>(PurposeProperty).HasColumnName("listing_purpose")
            .HasConversion<string>().HasMaxLength(16).IsRequired().ValueGeneratedNever();
        builder.Property(x => x.BuyerUserId).HasColumnName("buyer_user_id");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.OfferedAmount).HasColumnName("offered_amount").HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(PurchaseRequest.MaxMessageLength);
        builder.HasOne<ListingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_purchase_requests_submission");
        // One open request per customer and listing; deciding or withdrawing it makes room for the next.
        builder.HasIndex(x => new { x.ListingId, x.BuyerUserId }).IsUnique()
            .HasFilter("status = 'Submitted'").HasDatabaseName("ux_marketplace_purchase_requests_open");
        builder.HasIndex(x => new { x.BuyerUserId, x.SubmittedAt }).HasDatabaseName("ix_marketplace_purchase_requests_buyer");
        builder.HasOne<PropertyListing>().WithMany()
            .HasForeignKey(nameof(PurchaseRequest.ListingId), nameof(PurchaseRequest.PropertyId), PurposeProperty)
            .HasPrincipalKey(nameof(PropertyListing.Id), nameof(PropertyListing.PropertyId), nameof(PropertyListing.Purpose))
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_purchase_requests_listing");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.BuyerUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_purchase_requests_buyer");
        builder.HasIndex(x => new { x.PropertyId, x.Status }).HasDatabaseName("ix_marketplace_purchase_requests_property_status");
    }
}
