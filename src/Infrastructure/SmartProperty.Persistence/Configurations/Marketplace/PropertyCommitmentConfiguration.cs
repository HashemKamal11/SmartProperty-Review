using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

/// <summary>
/// The dated commitments. Two rules are enforced by the database itself, beyond the checks the handlers make first:
/// no two active rentals of a property overlap, and a property has at most one active sale. Both are exclusion
/// constraints added by the migration (EF cannot model them) and both are deferred to commit, so a unit of work may
/// expire an overdue commitment and commit its replacement together.
/// </summary>
internal sealed class PropertyCommitmentConfiguration : IEntityTypeConfiguration<PropertyCommitment>
{
    public void Configure(EntityTypeBuilder<PropertyCommitment> builder)
    {
        builder.ToTable("property_commitments", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_commitments_state",
                "kind IN ('Sale', 'Rental') AND status IN ('PendingSignature', 'Signed', 'Occupied', 'Completed', 'Expired')");
            table.HasCheckConstraint("ck_marketplace_commitments_money",
                "amount > 0 AND currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_marketplace_commitments_shape",
                "(kind = 'Sale' AND purchase_request_id IS NOT NULL AND rental_request_id IS NULL AND pricing_period IS NULL AND start_date IS NULL AND end_date IS NULL AND check_in_time IS NULL AND check_out_time IS NULL AND time_zone_id IS NULL) "
                + "OR (kind = 'Rental' AND rental_request_id IS NOT NULL AND purchase_request_id IS NULL AND pricing_period IN ('Daily', 'Weekly', 'Monthly', 'Yearly') AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date > start_date AND check_in_time IS NOT NULL AND check_out_time IS NOT NULL AND time_zone_id IS NOT NULL)");
            table.HasCheckConstraint("ck_marketplace_commitments_history",
                "signing_deadline > accepted_at AND updated_at >= accepted_at AND ("
                + "(status = 'PendingSignature' AND signed_at IS NULL AND handed_over_at IS NULL AND completed_at IS NULL AND expired_at IS NULL) "
                + "OR (status = 'Signed' AND signed_at IS NOT NULL AND handed_over_at IS NULL AND completed_at IS NULL AND expired_at IS NULL) "
                + "OR (status = 'Occupied' AND kind = 'Rental' AND signed_at IS NOT NULL AND handed_over_at IS NOT NULL AND completed_at IS NULL AND expired_at IS NULL) "
                + "OR (status = 'Completed' AND signed_at IS NOT NULL AND completed_at IS NOT NULL AND expired_at IS NULL) "
                + "OR (status = 'Expired' AND expired_at IS NOT NULL AND signed_at IS NULL AND handed_over_at IS NULL AND completed_at IS NULL))");
        });

        builder.HasKey(x => x.Id).HasName("pk_marketplace_property_commitments");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.PropertyId).HasColumnName("property_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24).IsConcurrencyToken();
        builder.Property(x => x.ListingId).HasColumnName("listing_id");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.PurchaseRequestId).HasColumnName("purchase_request_id");
        builder.Property(x => x.RentalRequestId).HasColumnName("rental_request_id");
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id");
        builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(x => x.PricingPeriod).HasColumnName("pricing_period").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.StartDate).HasColumnName("start_date");
        builder.Property(x => x.EndDate).HasColumnName("end_date");
        builder.Property(x => x.CheckInTime).HasColumnName("check_in_time");
        builder.Property(x => x.CheckOutTime).HasColumnName("check_out_time");
        builder.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64);
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(x => x.SigningDeadline).HasColumnName("signing_deadline");
        builder.Property(x => x.SignedAt).HasColumnName("signed_at");
        builder.Property(x => x.HandedOverAt).HasColumnName("handed_over_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.ExpiredAt).HasColumnName("expired_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_property");
        builder.HasOne<PropertyListing>().WithMany().HasForeignKey(x => x.ListingId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_listing");
        builder.HasOne<ListingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_submission");
        builder.HasOne<PurchaseRequest>().WithMany().HasForeignKey(x => x.PurchaseRequestId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_purchase_request");
        builder.HasOne<RentalRequest>().WithMany().HasForeignKey(x => x.RentalRequestId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_rental_request");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.CustomerUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_customer");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_commitments_owner");

        // A request is committed to at most once.
        builder.HasIndex(x => x.PurchaseRequestId).IsUnique().HasFilter("purchase_request_id IS NOT NULL")
            .HasDatabaseName("ux_marketplace_commitments_purchase_request");
        builder.HasIndex(x => x.RentalRequestId).IsUnique().HasFilter("rental_request_id IS NOT NULL")
            .HasDatabaseName("ux_marketplace_commitments_rental_request");
        builder.HasIndex(x => new { x.PropertyId, x.Status }).HasDatabaseName("ix_marketplace_commitments_property_status");
        builder.HasIndex(x => new { x.CustomerUserId, x.AcceptedAt }).HasDatabaseName("ix_marketplace_commitments_customer");
        builder.HasIndex(x => new { x.OwnerUserId, x.AcceptedAt }).HasDatabaseName("ix_marketplace_commitments_owner");
    }
}

internal sealed class MarketplaceSettingsConfiguration : IEntityTypeConfiguration<MarketplaceSettings>
{
    public void Configure(EntityTypeBuilder<MarketplaceSettings> builder)
    {
        builder.ToTable("settings", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_settings_singleton", "id = 1");
            table.HasCheckConstraint("ck_marketplace_settings_signing_window", "signing_window_hours BETWEEN 1 AND 720");
        });
        builder.HasKey(x => x.Id).HasName("pk_marketplace_settings");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SigningWindowHours).HasColumnName("signing_window_hours");
        builder.Property(x => x.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_settings_updated_by");
    }
}
