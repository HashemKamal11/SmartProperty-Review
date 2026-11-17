using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Configurations.Marketplace;

internal sealed class AuthorityRequestConfiguration : IEntityTypeConfiguration<AuthorityRequest>
{
    /// <summary>At most one open request (draft, legal review or issuance) per property.</summary>
    public const string OpenRequestUniqueIndexName = "ux_marketplace_authority_requests_open";

    /// <summary>At most one open ownership-transfer request per claimant and property.</summary>
    public const string OpenTransferUniqueIndexName = "ux_marketplace_authority_requests_open_transfer";

    public void Configure(EntityTypeBuilder<AuthorityRequest> builder)
    {
        builder.ToTable("authority_requests", "marketplace", table =>
        {
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_status",
                "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance', 'Approved', 'Rejected', 'Withdrawn')");
            table.HasCheckConstraint("ck_marketplace_authority_requests_purposes", "requests_sale OR requests_rent");

            // Only an ownership transfer names a previous owner or a platform sale, and never its own requester.
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_kind",
                "(kind = 'Listing' AND previous_owner_user_id IS NULL AND sale_commitment_id IS NULL)"
                + " OR (kind = 'OwnershipTransfer' AND (previous_owner_user_id IS NULL OR previous_owner_user_id <> requester_user_id))");
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_previous_owner_independence",
                "previous_owner_user_id IS NULL OR ("
                + "(legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> previous_owner_user_id)"
                + " AND (issued_by_user_id IS NULL OR issued_by_user_id <> previous_owner_user_id)"
                + " AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> previous_owner_user_id)"
                + " AND (completion_requested_by_user_id IS NULL OR completion_requested_by_user_id <> previous_owner_user_id))");

            // Chosen during the draft, required from submission on. An owner gives only the deed number; a
            // representative also gives the license, the agency and the owner.
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_capacity",
                "(capacity IS NULL AND ownership_document_number IS NULL AND brokerage_license_number IS NULL"
                + " AND agency_name IS NULL AND property_owner_name IS NULL AND status IN ('Draft', 'Withdrawn'))"
                + " OR (capacity = 'Owner' AND length(btrim(ownership_document_number)) > 0"
                + " AND brokerage_license_number IS NULL AND agency_name IS NULL AND property_owner_name IS NULL)"
                + " OR (capacity = 'IndividualRepresentative' AND length(btrim(ownership_document_number)) > 0"
                + " AND length(btrim(brokerage_license_number)) > 0 AND length(btrim(agency_name)) > 0"
                + " AND length(btrim(property_owner_name)) > 0)");

            // Listing content only for requested purposes, and present for each of them from submission on.
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_listings",
                "(requests_sale OR sale_listing_content IS NULL) AND (requests_rent OR rent_listing_content IS NULL)"
                + " AND (status IN ('Draft', 'Withdrawn')"
                + " OR ((NOT requests_sale OR sale_listing_content IS NOT NULL) AND (NOT requests_rent OR rent_listing_content IS NOT NULL)))");

            // Each stage carries exactly its own facts. A request asked to complete is a draft again (no submission; no
            // legal review, unless the administrator asked, who gets it back with the legal approval kept); it can be withdrawn from any open stage, so a withdrawn one may carry earlier facts. A
            // rejection is final and keeps the submission it decided on.
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_stages",
                "(status = 'Draft' AND submitted_at IS NULL)"
                + " OR status = 'Withdrawn'"
                + " OR (status IN ('PendingLegalReview', 'PendingIssuance', 'Approved', 'Rejected') AND submitted_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_legal_review",
                "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL)"
                + " AND (legal_reviewed_by_user_id IS NULL) = (reviewed_property_updated_at IS NULL)"
                + " AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL)"
                + " AND (status <> 'PendingLegalReview' OR legal_reviewed_by_user_id IS NULL)"
                + " AND (status <> 'Draft' OR legal_reviewed_by_user_id IS NULL OR completion_requested_at_stage = 'PendingIssuance')");
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_outcomes",
                "(status = 'Approved') = (issued_by_user_id IS NOT NULL AND issued_at IS NOT NULL AND grant_valid_until IS NOT NULL)"
                + " AND (status = 'Rejected') = (rejected_by_user_id IS NOT NULL AND rejected_at IS NOT NULL"
                + " AND rejection_reason IS NOT NULL AND rejected_at_stage IN ('PendingLegalReview', 'PendingIssuance'))"
                + " AND (rejected_by_user_id IS NOT NULL OR (rejected_at IS NULL AND rejection_reason IS NULL AND rejected_at_stage IS NULL))"
                + " AND ((completion_requested_by_user_id IS NULL AND completion_requested_at IS NULL"
                + " AND completion_request_reason IS NULL AND completion_requested_at_stage IS NULL)"
                + " OR (completion_requested_by_user_id IS NOT NULL AND completion_requested_at IS NOT NULL"
                + " AND completion_request_reason IS NOT NULL AND completion_requested_at_stage IN ('PendingLegalReview', 'PendingIssuance')))"
                + " AND (status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_independence",
                "(legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> requester_user_id)"
                + " AND (issued_by_user_id IS NULL OR (issued_by_user_id <> requester_user_id AND issued_by_user_id <> legal_reviewed_by_user_id))"
                + " AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> requester_user_id)"
                + " AND (completion_requested_by_user_id IS NULL OR completion_requested_by_user_id <> requester_user_id)");
            table.HasCheckConstraint(
                "ck_marketplace_authority_requests_grants",
                "(status = 'Approved') = (sale_grant_id IS NOT NULL OR rent_grant_id IS NOT NULL)"
                + " AND (status <> 'Approved' OR ((sale_grant_id IS NOT NULL) = requests_sale AND (rent_grant_id IS NOT NULL) = requests_rent))");
        });

        builder.HasKey(request => request.Id);

        builder.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(request => request.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(request => request.RequesterUserId).HasColumnName("requester_user_id").IsRequired();
        builder.Property(request => request.RequesterProfileId).HasColumnName("requester_profile_id").IsRequired();
        builder.Property(request => request.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(request => request.PreviousOwnerUserId).HasColumnName("previous_owner_user_id");
        builder.Property(request => request.SaleCommitmentId).HasColumnName("sale_commitment_id");
        builder.Ignore(request => request.IsOwnershipTransfer);
        builder.Property(request => request.RequestsSale).HasColumnName("requests_sale").IsRequired();
        builder.Property(request => request.RequestsRent).HasColumnName("requests_rent").IsRequired();
        builder.Property(request => request.Capacity).HasColumnName("capacity").HasConversion<string>().HasMaxLength(32);
        builder.Property(request => request.OwnershipDocumentNumber)
            .HasColumnName("ownership_document_number").HasMaxLength(AuthorityRequest.MaxNumberLength);
        builder.Property(request => request.SaleListingContent)
            .HasColumnName("sale_listing_content").HasColumnType("jsonb");
        builder.Property(request => request.RentListingContent)
            .HasColumnName("rent_listing_content").HasColumnType("jsonb");
        builder.OwnsOne(request => request.Representative, representative =>
        {
            representative.Property(details => details.BrokerageLicenseNumber)
                .HasColumnName("brokerage_license_number").HasMaxLength(AuthorityRequest.MaxNumberLength).IsRequired();
            representative.Property(details => details.AgencyName)
                .HasColumnName("agency_name").HasMaxLength(AuthorityRequest.MaxNameLength).IsRequired();
            representative.Property(details => details.PropertyOwnerName)
                .HasColumnName("property_owner_name").HasMaxLength(AuthorityRequest.MaxNameLength).IsRequired();
        });
        builder.Property(request => request.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(request => request.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(request => request.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(request => request.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(request => request.LegalReviewedByUserId).HasColumnName("legal_reviewed_by_user_id");
        builder.Property(request => request.LegalReviewedAt).HasColumnName("legal_reviewed_at");
        builder.Property(request => request.LegalReviewNote).HasColumnName("legal_review_note").HasMaxLength(AuthorityRequest.MaxTextLength);
        builder.Property(request => request.IssuedByUserId).HasColumnName("issued_by_user_id");
        builder.Property(request => request.IssuedAt).HasColumnName("issued_at");
        builder.Property(request => request.GrantValidUntil).HasColumnName("grant_valid_until");
        builder.Property(request => request.SaleGrantId).HasColumnName("sale_grant_id");
        builder.Property(request => request.RentGrantId).HasColumnName("rent_grant_id");
        builder.Property(request => request.RejectedByUserId).HasColumnName("rejected_by_user_id");
        builder.Property(request => request.RejectedAt).HasColumnName("rejected_at");
        builder.Property(request => request.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(AuthorityRequest.MaxTextLength);
        builder.Property(request => request.RejectedAtStage).HasColumnName("rejected_at_stage").HasConversion<string>().HasMaxLength(32);
        builder.Property(request => request.WithdrawnAt).HasColumnName("withdrawn_at");
        builder.Property(request => request.ReviewedPropertyUpdatedAt).HasColumnName("reviewed_property_updated_at");
        builder.Property(request => request.CompletionRequestedByUserId).HasColumnName("completion_requested_by_user_id");
        builder.Property(request => request.CompletionRequestedAt).HasColumnName("completion_requested_at");
        builder.Property(request => request.CompletionRequestReason)
            .HasColumnName("completion_request_reason").HasMaxLength(AuthorityRequest.MaxTextLength);
        builder.Property(request => request.CompletionRequestedAtStage)
            .HasColumnName("completion_requested_at_stage").HasConversion<string>().HasMaxLength(32);
        builder.Property(request => request.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();

        // Requests, document versions and decisions are never deleted; the database foreign keys never cascade.
        builder.HasMany(request => request.Documents)
            .WithOne()
            .HasForeignKey(document => document.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(request => request.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(request => request.Decisions)
            .WithOne()
            .HasForeignKey(decision => decision.RequestId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(request => request.Decisions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(request => request.CurrentDocuments);
        builder.Ignore(request => request.Missing);
        builder.Ignore(request => request.Purposes);
        builder.Ignore(request => request.IsOpen);

        // Users are deactivated and properties archived, never deleted, so nothing cascades from them.
        builder.HasOne<Property>().WithMany().HasForeignKey(request => request.PropertyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.LegalReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.IssuedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.RejectedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.CompletionRequestedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany().HasForeignKey(request => request.PreviousOwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertyCommitment>().WithMany().HasForeignKey(request => request.SaleCommitmentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(request => request.PropertyId)
            .IsUnique()
            .HasFilter("status IN ('Draft', 'PendingLegalReview', 'PendingIssuance') AND kind = 'Listing'")
            .HasDatabaseName(OpenRequestUniqueIndexName);

        // Ownership claims never block the owner's own requests (nor each other's claimants): one open claim per
        // claimant and property, so a false claim cannot stop the real owner.
        builder.HasIndex(request => new { request.PropertyId, request.RequesterUserId })
            .IsUnique()
            .HasFilter("status IN ('Draft', 'PendingLegalReview', 'PendingIssuance') AND kind = 'OwnershipTransfer'")
            .HasDatabaseName(OpenTransferUniqueIndexName);

        // The staff queue filters by status and reads in submission order; the owner lists their own.
        builder.HasIndex(request => new { request.Status, request.SubmittedAt });
        builder.HasIndex(request => new { request.RequesterUserId, request.CreatedAt });
    }
}

internal sealed class AuthorityRequestDocumentConfiguration : IEntityTypeConfiguration<AuthorityRequestDocument>
{
    public const string CurrentVersionUniqueIndexName = "ux_marketplace_authority_request_documents_current";

    public void Configure(EntityTypeBuilder<AuthorityRequestDocument> builder)
    {
        builder.ToTable("authority_request_documents", "marketplace", table =>
        {
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_documents_type",
                "document_type IN ('TitleDeed', 'SaleContract', 'UtilityBill', 'Other', 'Authorization', 'BrokerageLicense')");
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_documents_content",
                $"content_type IN ('image/jpeg', 'image/png', 'image/webp', 'application/pdf') AND size_bytes > 0 AND size_bytes <= {AuthorityRequest.MaxDocumentBytes}");
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_documents_scan",
                "scan_status IN ('PendingScan', 'Clean', 'Rejected', 'ScanFailed') AND scan_attempts >= 0"
                + " AND ((scan_status = 'PendingScan') = (next_scan_at IS NOT NULL))"
                + " AND (scan_status <> 'Clean' OR (scanned_at IS NOT NULL AND storage_key LIKE 'documents/%'))"
                + " AND (scan_status = 'Clean' OR storage_key NOT LIKE 'documents/%')");
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_documents_version",
                "version_number >= 1 AND sha256 ~ '^[0-9A-F]{64}$'");
        });

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(document => document.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(document => document.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(document => document.VersionNumber).HasColumnName("version_number").IsRequired();
        builder.Property(document => document.Type).HasColumnName("document_type").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(document => document.StorageKey).HasColumnName("storage_key").HasMaxLength(AuthorityRequest.MaxStorageKeyLength).IsRequired();
        builder.Property(document => document.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
        builder.Property(document => document.FileName).HasColumnName("file_name").HasMaxLength(AuthorityRequest.MaxFileNameLength);
        builder.Property(document => document.SizeBytes).HasColumnName("size_bytes").IsRequired();
        builder.Property(document => document.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        builder.Property(document => document.UploadedByUserId).HasColumnName("uploaded_by_user_id").IsRequired();
        builder.Property(document => document.UploadedAt).HasColumnName("uploaded_at").IsRequired();
        builder.Property(document => document.SupersededAt).HasColumnName("superseded_at");
        builder.Property(document => document.RemovedAt).HasColumnName("removed_at");
        builder.Property(document => document.ScanStatus).HasColumnName("scan_status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(document => document.ScanAttempts).HasColumnName("scan_attempts").IsRequired();
        builder.Property(document => document.NextScanAt).HasColumnName("next_scan_at");
        builder.Property(document => document.ScannedAt).HasColumnName("scanned_at");
        builder.Property(document => document.ScanEngine).HasColumnName("scan_engine").HasMaxLength(AuthorityRequestDocument.MaxScanDetailLength);
        builder.Property(document => document.ScanDetail).HasColumnName("scan_detail").HasMaxLength(AuthorityRequestDocument.MaxScanDetailLength);
        builder.Property(document => document.RowVersion).HasColumnName("row_version").IsConcurrencyToken().IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(document => document.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(document => document.StorageKey).IsUnique();
        builder.HasIndex(document => new { document.DocumentId, document.VersionNumber }).IsUnique();

        // One version in force per document.
        builder.HasIndex(document => document.DocumentId)
            .IsUnique()
            .HasFilter("superseded_at IS NULL AND removed_at IS NULL")
            .HasDatabaseName(CurrentVersionUniqueIndexName);

        // The scan worker's queue.
        builder.HasIndex(document => document.NextScanAt).HasFilter("scan_status = 'PendingScan'");
    }
}

internal sealed class AuthorityRequestDecisionConfiguration : IEntityTypeConfiguration<AuthorityRequestDecision>
{
    public void Configure(EntityTypeBuilder<AuthorityRequestDecision> builder)
    {
        builder.ToTable("authority_request_decisions", "marketplace", table =>
        {
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_decisions_kind",
                "kind IN ('Submitted', 'LegalApproved', 'LegalCompletionRequested', 'LegalRejected', 'AdminApproved',"
                + " 'AdminCompletionRequested', 'AdminRejected', 'Withdrawn', 'ClosedOwnershipTransferred')");

            // Every staff decision carries the reviewer's no-conflict confirmation; rejections and completion requests a reason.
            table.HasCheckConstraint(
                "ck_marketplace_authority_request_decisions_staff",
                "(kind IN ('Submitted', 'Withdrawn', 'ClosedOwnershipTransferred') OR attested_no_conflict)"
                + " AND (kind NOT IN ('LegalCompletionRequested', 'LegalRejected', 'AdminCompletionRequested', 'AdminRejected', 'ClosedOwnershipTransferred')"
                + " OR length(btrim(reason)) > 0)");
        });

        builder.HasKey(decision => decision.Id);

        builder.Property(decision => decision.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(decision => decision.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(decision => decision.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(decision => decision.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(decision => decision.At).HasColumnName("at").IsRequired();
        builder.Property(decision => decision.Reason).HasColumnName("reason").HasMaxLength(AuthorityRequest.MaxTextLength);
        builder.Property(decision => decision.RequestVersion).HasColumnName("request_version").IsRequired();
        builder.Property(decision => decision.AttestedNoConflict).HasColumnName("attested_no_conflict").IsRequired();
        builder.Property(decision => decision.DocumentVersionIds).HasColumnName("document_version_ids").HasColumnType("uuid[]").IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(decision => decision.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(decision => new { decision.RequestId, decision.At });
    }
}
