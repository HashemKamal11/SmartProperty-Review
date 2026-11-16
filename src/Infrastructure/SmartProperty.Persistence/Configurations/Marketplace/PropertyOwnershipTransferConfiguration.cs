using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class PropertyOwnershipTransferConfiguration : IEntityTypeConfiguration<PropertyOwnershipTransfer>
{
    public void Configure(EntityTypeBuilder<PropertyOwnershipTransfer> builder)
    {
        builder.ToTable("property_ownership_transfers", "marketplace", table =>
        {
            table.HasCheckConstraint(
                "ck_marketplace_ownership_transfers_parties",
                "(previous_owner_user_id IS NULL OR previous_owner_user_id <> new_owner_user_id)"
                + " AND issued_by_user_id <> new_owner_user_id"
                + " AND (previous_owner_user_id IS NULL OR issued_by_user_id <> previous_owner_user_id)");
        });

        builder.HasKey(transfer => transfer.Id).HasName("pk_marketplace_ownership_transfers");

        builder.Property(transfer => transfer.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(transfer => transfer.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(transfer => transfer.PreviousOwnerUserId).HasColumnName("previous_owner_user_id");
        builder.Property(transfer => transfer.NewOwnerUserId).HasColumnName("new_owner_user_id").IsRequired();
        builder.Property(transfer => transfer.AuthorityRequestId).HasColumnName("authority_request_id").IsRequired();
        builder.Property(transfer => transfer.SaleCommitmentId).HasColumnName("sale_commitment_id");
        builder.Property(transfer => transfer.IssuedByUserId).HasColumnName("issued_by_user_id").IsRequired();
        builder.Property(transfer => transfer.TransferredAt).HasColumnName("transferred_at").IsRequired();

        // History is never deleted; nothing cascades.
        builder.HasOne<Property>().WithMany().HasForeignKey(transfer => transfer.PropertyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(transfer => transfer.PreviousOwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(transfer => transfer.NewOwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(transfer => transfer.IssuedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AuthorityRequest>().WithMany().HasForeignKey(transfer => transfer.AuthorityRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertyCommitment>().WithMany().HasForeignKey(transfer => transfer.SaleCommitmentId).OnDelete(DeleteBehavior.Restrict);

        // One transfer per approved request; the history reads in order per property.
        builder.HasIndex(transfer => transfer.AuthorityRequestId).IsUnique().HasDatabaseName("ux_marketplace_ownership_transfers_request");
        builder.HasIndex(transfer => new { transfer.PropertyId, transfer.TransferredAt });
    }
}
