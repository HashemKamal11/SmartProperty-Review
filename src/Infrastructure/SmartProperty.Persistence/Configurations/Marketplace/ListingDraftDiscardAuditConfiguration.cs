using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Persistence.Entities.Marketplace;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class ListingDraftDiscardAuditConfiguration : IEntityTypeConfiguration<ListingDraftDiscardAudit>
{
    public void Configure(EntityTypeBuilder<ListingDraftDiscardAudit> b)
    {
        b.ToTable("draft_discard_audits", "marketplace", t =>
        {
            t.HasCheckConstraint("ck_marketplace_discard_audits_identity",
                "event_id <> '00000000-0000-0000-0000-000000000000'::uuid AND actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND submission_id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_marketplace_discard_audits_values",
                "purpose IN ('Sale','Rent') AND version > 0 AND revision > 0 AND isfinite(occurred_at) AND occurred_at <> '0001-01-01 00:00:00+00'::timestamptz");
        });
        b.HasKey(x => x.EventId).HasName("pk_marketplace_discard_audits");
        b.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        b.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        b.Property(x => x.SubmissionId).HasColumnName("submission_id");
        b.Property(x => x.ListingId).HasColumnName("listing_id");
        b.Property(x => x.PropertyId).HasColumnName("property_id");
        b.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Revision).HasColumnName("revision");
        b.Property(x => x.ListingDeleted).HasColumnName("listing_deleted");
        b.HasIndex(x => x.SubmissionId).IsUnique().HasDatabaseName("ux_marketplace_discard_audits_submission");
        b.HasIndex(x => new { x.PropertyId, x.OccurredAt }).HasDatabaseName("ix_marketplace_discard_audits_property_time");
    }
}
