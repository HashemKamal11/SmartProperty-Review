using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

/// <summary>
/// One purpose-specific advertising authority for one user on one property. A row is only ever created from a grant
/// the Domain validated when it was issued, and is immutable afterwards except for its revocation.
/// </summary>
/// <remarks>
/// Nothing here creates a grant. Who may issue one, and what the legal review it cites is, is not decided yet, so no
/// endpoint or handler issues grants. The table exists so the listing write path has a trusted place to read them from.
/// </remarks>
internal sealed class PropertyAuthorityGrantConfiguration : IEntityTypeConfiguration<PropertyAuthorityGrant>
{
    private const string EmptyGuid = "'00000000-0000-0000-0000-000000000000'::uuid";

    public void Configure(EntityTypeBuilder<PropertyAuthorityGrant> b)
    {
        b.ToTable("authority_grants", "marketplace", t =>
        {
            t.HasCheckConstraint("ck_marketplace_authority_grants_ids",
                $"id <> {EmptyGuid} AND property_id <> {EmptyGuid} AND grantee_user_id <> {EmptyGuid} AND legal_review_reference_id <> {EmptyGuid} AND issued_by_user_id <> {EmptyGuid} AND legal_reviewed_by_user_id <> {EmptyGuid}");
            t.HasCheckConstraint("ck_marketplace_authority_grants_values",
                "purpose IN ('Sale','Rent') AND version > 0 AND legal_reviewed_at <= issued_at AND isfinite(issued_at) AND isfinite(legal_reviewed_at)");
            // The independence rules the Domain enforces on issue, kept even if a row is written around the Domain.
            t.HasCheckConstraint("ck_marketplace_authority_grants_independence",
                "issued_by_user_id <> grantee_user_id AND legal_reviewed_by_user_id <> grantee_user_id AND legal_reviewed_by_user_id <> issued_by_user_id");
            t.HasCheckConstraint("ck_marketplace_authority_grants_revocation",
                "(revoked_at IS NULL AND revoked_by_user_id IS NULL AND version = 1) OR (revoked_at IS NOT NULL AND revoked_by_user_id IS NOT NULL AND revoked_at >= issued_at AND version > 1)");
            // The lookup columns must say what the stored authority says. Only members that cast safely are compared.
            t.HasCheckConstraint("ck_marketplace_authority_grants_snapshots",
                "COALESCE((authority_snapshot->>'FormatVersion' = '1' AND issuer_snapshot->>'FormatVersion' = '1' AND legal_reviewer_snapshot->>'FormatVersion' = '1'"
                + " AND authority_snapshot->'Payload'->>'PropertyId' = property_id::text"
                + " AND authority_snapshot->'Payload'->>'ActingUserId' = grantee_user_id::text"
                + " AND authority_snapshot->'Payload'->>'Purpose' = (CASE purpose WHEN 'Sale' THEN '1' ELSE '2' END)"
                + " AND authority_snapshot->'Payload'->>'Representation' IN ('1','2')"
                + " AND authority_snapshot->'Payload'->'WorkspaceId' = 'null'::jsonb"
                + " AND jsonb_typeof(authority_snapshot->'Payload'->'ValidUntil') = 'string'"
                + " AND issuer_snapshot->'Payload'->>'ReviewerUserId' = issued_by_user_id::text"
                + " AND legal_reviewer_snapshot->'Payload'->>'ReviewerUserId' = legal_reviewed_by_user_id::text), false)");
        });

        b.HasKey(x => x.Id).HasName("pk_marketplace_authority_grants");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.PropertyId).HasColumnName("property_id");
        b.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.GranteeUserId).HasColumnName("grantee_user_id");
        b.Property(x => x.IssuedByUserId).HasColumnName("issued_by_user_id");
        b.Property(x => x.LegalReviewReferenceId).HasColumnName("legal_review_reference_id");
        b.Property(x => x.LegalReviewedByUserId).HasColumnName("legal_reviewed_by_user_id");
        b.Property(x => x.LegalReviewedAt).HasColumnName("legal_reviewed_at");
        b.Property(x => x.IssuedAt).HasColumnName("issued_at");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.RevokedByUserId).HasColumnName("revoked_by_user_id");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Authority).IsRequired(), "authority_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Issuer).IsRequired(), "issuer_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.LegalReviewer).IsRequired(), "legal_reviewer_snapshot");

        // At most one grant in force per user, property and purpose. Revocation is permanent, so a revoked row stays as
        // history and a new grant is a new row.
        b.HasIndex(x => new { x.GranteeUserId, x.PropertyId, x.Purpose })
            .IsUnique()
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("ux_marketplace_authority_grants_active");
        b.HasIndex(x => new { x.PropertyId, x.GranteeUserId, x.Purpose, x.IssuedAt })
            .HasDatabaseName("ix_marketplace_authority_grants_lookup");

        b.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_authority_grants_property");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.GranteeUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_authority_grants_grantee");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.IssuedByUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_authority_grants_issuer");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.LegalReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_authority_grants_legal_reviewer");
        b.HasOne<User>().WithMany().HasForeignKey(x => x.RevokedByUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_authority_grants_revoker");
    }
}
