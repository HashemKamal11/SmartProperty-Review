using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class PropertyMarketplaceReservationConfiguration : IEntityTypeConfiguration<PropertyMarketplaceReservation>
{
    internal const string PurchaseWinnerProperty = "PurchaseWinnerId";
    internal const string RentalWinnerProperty = "RentalWinnerId";

    public void Configure(EntityTypeBuilder<PropertyMarketplaceReservation> builder)
    {
        builder.ToTable("property_reservations", "marketplace", table =>
        {
            table.HasCheckConstraint("ck_marketplace_property_reservations_ids",
                "property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND winning_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND winning_request_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_marketplace_property_reservations_purpose", "reserved_purpose IN ('Sale', 'Rent')");
            table.HasCheckConstraint("ck_marketplace_property_reservations_winner",
                "(reserved_purpose = 'Sale' AND purchase_winner_id IS NOT NULL AND purchase_winner_id = winning_request_id AND rental_winner_id IS NULL) OR (reserved_purpose = 'Rent' AND rental_winner_id IS NOT NULL AND rental_winner_id = winning_request_id AND purchase_winner_id IS NULL)");
        });
        builder.HasKey(x => x.PropertyId).HasName("pk_marketplace_property_reservations");
        builder.Property(x => x.PropertyId).HasColumnName("property_id").ValueGeneratedNever();
        builder.Property(x => x.ReservedPurpose).HasColumnName("reserved_purpose").HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.WinningListingId).HasColumnName("winning_listing_id");
        builder.Property(x => x.WinningRequestId).HasColumnName("winning_request_id");
        builder.Property(x => x.ReservedAt).HasColumnName("reserved_at");
        builder.Property<Guid?>(PurchaseWinnerProperty).HasColumnName("purchase_winner_id").ValueGeneratedNever();
        builder.Property<Guid?>(RentalWinnerProperty).HasColumnName("rental_winner_id").ValueGeneratedNever();
        builder.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_property_reservations_property");
        builder.HasOne<PropertyListing>().WithMany()
            .HasForeignKey(x => new { x.WinningListingId, x.PropertyId, x.ReservedPurpose })
            .HasPrincipalKey(x => new { x.Id, x.PropertyId, x.Purpose })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_property_reservations_listing");
        builder.HasOne<PurchaseRequest>().WithMany()
            .HasForeignKey(PurchaseWinnerProperty, nameof(PropertyMarketplaceReservation.WinningListingId), nameof(PropertyMarketplaceReservation.PropertyId))
            .HasPrincipalKey(nameof(PurchaseRequest.Id), nameof(PurchaseRequest.ListingId), nameof(PurchaseRequest.PropertyId))
            .IsRequired(false).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_reservations_purchase_winner");
        builder.HasOne<RentalRequest>().WithMany()
            .HasForeignKey(RentalWinnerProperty, nameof(PropertyMarketplaceReservation.WinningListingId), nameof(PropertyMarketplaceReservation.PropertyId))
            .HasPrincipalKey(nameof(RentalRequest.Id), nameof(RentalRequest.ListingId), nameof(RentalRequest.PropertyId))
            .IsRequired(false).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_reservations_rental_winner");
    }
}
