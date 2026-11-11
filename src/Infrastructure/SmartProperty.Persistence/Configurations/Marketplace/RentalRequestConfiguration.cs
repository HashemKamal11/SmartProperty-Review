using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class RentalRequestConfiguration : IEntityTypeConfiguration<RentalRequest>
{
    internal const string PurposeProperty = "ListingPurpose";

    public void Configure(EntityTypeBuilder<RentalRequest> builder)
    {
        builder.ToTable("rental_requests", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_rental_requests_ids",
                "id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND renter_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_marketplace_rental_requests_state",
                "listing_purpose = 'Rent' AND status IN ('Submitted', 'Accepted', 'Rejected', 'WithdrawnByRequester', 'ClosedListingUnavailable')");
            // A request from before dated rentals has none of the terms; a new one has all of them.
            table.HasCheckConstraint("ck_marketplace_rental_requests_terms",
                "(submission_id IS NULL AND pricing_period IS NULL AND amount IS NULL AND currency IS NULL AND start_date IS NULL AND end_date IS NULL AND check_in_time IS NULL AND check_out_time IS NULL AND time_zone_id IS NULL) "
                + "OR (submission_id IS NOT NULL AND pricing_period IN ('Daily', 'Weekly', 'Monthly', 'Yearly') AND amount > 0 AND currency ~ '^[A-Z]{3}$' AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date > start_date AND check_in_time IS NOT NULL AND check_out_time IS NOT NULL AND time_zone_id IS NOT NULL)");
            table.HasCheckConstraint("ck_marketplace_rental_requests_history",
                "updated_at >= submitted_at AND ((status = 'Submitted' AND decided_at IS NULL) OR (status <> 'Submitted' AND decided_at IS NOT NULL AND decided_at >= submitted_at AND decided_at = updated_at))");
        });
        builder.HasKey(x => x.Id).HasName("pk_marketplace_rental_requests");
        builder.HasAlternateKey(x => new { x.Id, x.ListingId, x.PropertyId }).HasName("ak_marketplace_rental_requests_identity");
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ListingId).HasColumnName("listing_id");
        builder.Property(x => x.PropertyId).HasColumnName("property_id");
        builder.Property<ListingPurpose?>(PurposeProperty).HasColumnName("listing_purpose")
            .HasConversion<string>().HasMaxLength(16).IsRequired().ValueGeneratedNever();
        builder.Property(x => x.RenterUserId).HasColumnName("renter_user_id");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsConcurrencyToken();
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.PricingPeriod).HasColumnName("pricing_period").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.StartDate).HasColumnName("start_date");
        builder.Property(x => x.EndDate).HasColumnName("end_date");
        builder.Property(x => x.CheckInTime).HasColumnName("check_in_time");
        builder.Property(x => x.CheckOutTime).HasColumnName("check_out_time");
        builder.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64);
        builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(RentalRequest.MaxMessageLength);
        builder.Ignore(x => x.IsDated);
        builder.HasOne<ListingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_rental_requests_submission");
        // One open request per customer and listing; deciding or withdrawing it makes room for the next.
        builder.HasIndex(x => new { x.ListingId, x.RenterUserId }).IsUnique()
            .HasFilter("status = 'Submitted'").HasDatabaseName("ux_marketplace_rental_requests_open");
        builder.HasIndex(x => new { x.RenterUserId, x.SubmittedAt }).HasDatabaseName("ix_marketplace_rental_requests_renter");
        builder.HasOne<PropertyListing>().WithMany()
            .HasForeignKey(nameof(RentalRequest.ListingId), nameof(RentalRequest.PropertyId), PurposeProperty)
            .HasPrincipalKey(nameof(PropertyListing.Id), nameof(PropertyListing.PropertyId), nameof(PropertyListing.Purpose))
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_rental_requests_listing");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.RenterUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_rental_requests_renter");
        builder.HasIndex(x => new { x.PropertyId, x.Status }).HasDatabaseName("ix_marketplace_rental_requests_property_status");
    }
}
