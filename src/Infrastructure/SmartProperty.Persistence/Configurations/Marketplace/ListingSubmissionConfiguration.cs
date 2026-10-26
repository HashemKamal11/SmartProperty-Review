using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class ListingSubmissionConfiguration : IEntityTypeConfiguration<ListingSubmission>
{
    public void Configure(EntityTypeBuilder<ListingSubmission> b)
    {
        b.ToTable("submissions", "marketplace", t =>
        {
            t.HasCheckConstraint("ck_marketplace_submissions_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND version > 0 AND ((version = 1 AND predecessor_id IS NULL) OR (version > 1 AND predecessor_id IS NOT NULL AND predecessor_id <> id))");
            t.HasCheckConstraint("ck_marketplace_submissions_state", "purpose IN ('Sale','Rent') AND status IN ('Draft','PendingReview','Approved','Rejected','Superseded') AND updated_at >= created_at AND ((status = 'Draft' AND submitted_at IS NULL) OR (status <> 'Draft' AND submitted_at IS NOT NULL AND submitted_at >= created_at AND submitted_at <= updated_at))");
            var fields = new[] { "Country", "Region", "City", "District", "Title", "Description", "Currency" };
            var required = string.Join(" AND ", fields.Select(f => $"jsonb_typeof(content_snapshot->'Payload'->'{f}') = 'string' AND length(btrim(content_snapshot->'Payload'->>'{f}')) > 0"));
            t.HasCheckConstraint("ck_marketplace_submissions_content", $"COALESCE((content_snapshot->>'FormatVersion' = '1' AND {required} AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2')))), false)");
            t.HasCheckConstraint("ck_marketplace_submissions_evidence", "COALESCE((jsonb_typeof(evidence_snapshot->'Payload') = 'array' AND jsonb_array_length(evidence_snapshot->'Payload') > 0 AND evidence_snapshot->>'FormatVersion' = '1' AND authority_snapshot->>'FormatVersion' = '1' AND submitted_assessments_snapshot->>'FormatVersion' = '1'), false)");
        });
        b.HasKey(x => x.Id).HasName("pk_marketplace_submissions");
        b.HasAlternateKey(x => new { x.Id, x.ListingId, x.PropertyId, x.Purpose }).HasName("ak_marketplace_submissions_identity");
        b.HasIndex(x => new { x.ListingId, x.Version }).IsUnique().HasDatabaseName("ux_marketplace_submissions_listing_version");
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.ListingId).HasColumnName("listing_id");
        b.Property(x => x.PropertyId).HasColumnName("property_id");
        b.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.PredecessorId).HasColumnName("predecessor_id");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Content).IsRequired(), "content_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Authority).IsRequired(), "authority_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.Evidence).IsRequired(), "evidence_snapshot");
        ListingSubmissionSnapshotConverter.Configure(b.Property(x => x.SubmittedAssessments).IsRequired(), "submitted_assessments_snapshot");
        b.HasOne<PropertyListing>().WithMany().HasForeignKey(x => new { x.ListingId, x.PropertyId, x.Purpose })
            .HasPrincipalKey(x => new { x.Id, x.PropertyId, x.Purpose }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_submissions_listing");
        b.HasOne<ListingSubmission>().WithMany().HasForeignKey(x => new { x.PredecessorId, x.ListingId, x.PropertyId, x.Purpose })
            .HasPrincipalKey(x => new { x.Id, x.ListingId, x.PropertyId, x.Purpose }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_submissions_predecessor");
        b.HasOne(x => x.ReviewDecision).WithOne().HasForeignKey<ListingReviewDecision>(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_marketplace_decisions_submission");
    }
}
