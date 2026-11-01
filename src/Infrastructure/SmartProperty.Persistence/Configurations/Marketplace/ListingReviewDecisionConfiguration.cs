using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class ListingReviewDecisionConfiguration : IEntityTypeConfiguration<ListingReviewDecision>
{
    public void Configure(EntityTypeBuilder<ListingReviewDecision> b)
    {
        b.ToTable("review_decisions", "marketplace", t =>
        {
            t.HasCheckConstraint("ck_marketplace_decisions_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_marketplace_decisions_reason", "approved OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
            t.HasCheckConstraint("ck_marketplace_decisions_snapshots", "COALESCE((reviewer_snapshot->>'FormatVersion' = '1' AND assessment_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(assessment_snapshot->'Payload') = 'array'), false)");
        });
        b.HasKey(x => x.Id).HasName("pk_marketplace_review_decisions");
        b.HasIndex(x => x.SubmissionId).IsUnique().HasDatabaseName("ux_marketplace_decisions_submission");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.SubmissionId).HasColumnName("submission_id");
        b.Property(x => x.Approved).HasColumnName("approved");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.DecidedAt).HasColumnName("decided_at");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Reviewer).IsRequired(), "reviewer_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Assessments).IsRequired(), "assessment_snapshot");
    }
}
