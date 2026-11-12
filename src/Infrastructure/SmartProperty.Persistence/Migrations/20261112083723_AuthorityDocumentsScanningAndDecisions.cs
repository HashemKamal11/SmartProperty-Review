using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityDocumentsScanningAndDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_authority_request_documents_authority_requests_request_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_independence",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_outcomes",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_stages",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_status",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.AddColumn<string>(
                name: "completion_request_reason",
                schema: "marketplace",
                table: "authority_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completion_requested_at",
                schema: "marketplace",
                table: "authority_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "completion_requested_at_stage",
                schema: "marketplace",
                table: "authority_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_property_updated_at",
                schema: "marketplace",
                table: "authority_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "document_id",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_scan_at",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "row_version",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "scan_attempts",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "scan_detail",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scan_engine",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scan_status",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scanned_at",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sha256",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "version_number",
                schema: "marketplace",
                table: "authority_request_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing rows (written before documents were versioned and scanned):
            // - each document becomes version 1 of itself, uploaded by the requester;
            // - its bytes were never scanned and its digest was never recorded, so it is ScanFailed with an all-zero
            //   digest placeholder and an explanation. It can never be served or approved; the owner replaces it.
            // - a "sent back" request (rejected_* on a Draft) is a completion request in the new model;
            // - a request past legal review gets reviewed_property_updated_at = legal_reviewed_at, which no property
            //   timestamp will match, so a pending final approval is refused until the request is reviewed again.
            migrationBuilder.Sql("""
                UPDATE marketplace.authority_request_documents d
                SET document_id = d.id,
                    version_number = 1,
                    uploaded_by_user_id = r.requester_user_id,
                    scan_status = 'ScanFailed',
                    scan_attempts = 0,
                    scanned_at = d.uploaded_at,
                    scan_detail = 'Uploaded before malware scanning existed; replace this document.',
                    sha256 = repeat('0', 64),
                    row_version = 1
                FROM marketplace.authority_requests r
                WHERE r.id = d.request_id;

                UPDATE marketplace.authority_requests
                SET completion_requested_by_user_id = rejected_by_user_id,
                    completion_requested_at = rejected_at,
                    completion_request_reason = rejection_reason,
                    completion_requested_at_stage = rejected_at_stage,
                    rejected_by_user_id = NULL,
                    rejected_at = NULL,
                    rejection_reason = NULL,
                    rejected_at_stage = NULL
                WHERE rejected_by_user_id IS NOT NULL;

                UPDATE marketplace.authority_requests
                SET reviewed_property_updated_at = legal_reviewed_at
                WHERE legal_reviewed_by_user_id IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "authority_request_decisions",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    request_version = table.Column<int>(type: "integer", nullable: false),
                    attested_no_conflict = table.Column<bool>(type: "boolean", nullable: false),
                    document_version_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_request_decisions", x => x.id);
                    table.CheckConstraint("ck_marketplace_authority_request_decisions_kind", "kind IN ('Submitted', 'LegalApproved', 'LegalCompletionRequested', 'LegalRejected', 'AdminApproved', 'AdminCompletionRequested', 'AdminRejected', 'Withdrawn')");
                    table.CheckConstraint("ck_marketplace_authority_request_decisions_staff", "(kind IN ('Submitted', 'Withdrawn') OR attested_no_conflict) AND (kind NOT IN ('LegalCompletionRequested', 'LegalRejected', 'AdminCompletionRequested', 'AdminRejected') OR length(btrim(reason)) > 0)");
                    table.ForeignKey(
                        name: "FK_authority_request_decisions_authority_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "marketplace",
                        principalTable: "authority_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authority_request_decisions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "completion_requested_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_independence",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> requester_user_id) AND (issued_by_user_id IS NULL OR (issued_by_user_id <> requester_user_id AND issued_by_user_id <> legal_reviewed_by_user_id)) AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> requester_user_id) AND (completion_requested_by_user_id IS NULL OR completion_requested_by_user_id <> requester_user_id)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL) AND (legal_reviewed_by_user_id IS NULL) = (reviewed_property_updated_at IS NULL) AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL) AND (status NOT IN ('Draft', 'PendingLegalReview') OR legal_reviewed_by_user_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_outcomes",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(status = 'Approved') = (issued_by_user_id IS NOT NULL AND issued_at IS NOT NULL AND grant_valid_until IS NOT NULL) AND (status = 'Rejected') = (rejected_by_user_id IS NOT NULL AND rejected_at IS NOT NULL AND rejection_reason IS NOT NULL AND rejected_at_stage IN ('PendingLegalReview', 'PendingIssuance')) AND (rejected_by_user_id IS NOT NULL OR (rejected_at IS NULL AND rejection_reason IS NULL AND rejected_at_stage IS NULL)) AND ((completion_requested_by_user_id IS NULL AND completion_requested_at IS NULL AND completion_request_reason IS NULL AND completion_requested_at_stage IS NULL) OR (completion_requested_by_user_id IS NOT NULL AND completion_requested_at IS NOT NULL AND completion_request_reason IS NOT NULL AND completion_requested_at_stage IN ('PendingLegalReview', 'PendingIssuance'))) AND (status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_stages",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(status = 'Draft' AND submitted_at IS NULL) OR status = 'Withdrawn' OR (status IN ('PendingLegalReview', 'PendingIssuance', 'Approved', 'Rejected') AND submitted_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_status",
                schema: "marketplace",
                table: "authority_requests",
                sql: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance', 'Approved', 'Rejected', 'Withdrawn')");

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_documents_document_id_version_number",
                schema: "marketplace",
                table: "authority_request_documents",
                columns: new[] { "document_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_documents_next_scan_at",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "next_scan_at",
                filter: "scan_status = 'PendingScan'");

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_documents_uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "uploaded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_request_documents_current",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "document_id",
                unique: true,
                filter: "superseded_at IS NULL AND removed_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_documents_scan",
                schema: "marketplace",
                table: "authority_request_documents",
                sql: "scan_status IN ('PendingScan', 'Clean', 'Rejected', 'ScanFailed') AND scan_attempts >= 0 AND ((scan_status = 'PendingScan') = (next_scan_at IS NOT NULL)) AND (scan_status <> 'Clean' OR (scanned_at IS NOT NULL AND storage_key LIKE 'documents/%')) AND (scan_status = 'Clean' OR storage_key NOT LIKE 'documents/%')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_documents_version",
                schema: "marketplace",
                table: "authority_request_documents",
                sql: "version_number >= 1 AND sha256 ~ '^[0-9A-F]{64}$'");

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_decisions_actor_user_id",
                schema: "marketplace",
                table: "authority_request_decisions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_decisions_request_id_at",
                schema: "marketplace",
                table: "authority_request_decisions",
                columns: new[] { "request_id", "at" });

            migrationBuilder.AddForeignKey(
                name: "FK_authority_request_documents_authority_requests_request_id",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "request_id",
                principalSchema: "marketplace",
                principalTable: "authority_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_authority_request_documents_users_uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "uploaded_by_user_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_authority_requests_users_completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "completion_requested_by_user_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_authority_request_documents_authority_requests_request_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropForeignKey(
                name: "FK_authority_request_documents_users_uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropForeignKey(
                name: "FK_authority_requests_users_completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropTable(
                name: "authority_request_decisions",
                schema: "marketplace");

            migrationBuilder.DropIndex(
                name: "IX_authority_requests_completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_independence",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_outcomes",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_stages",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_status",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropIndex(
                name: "IX_authority_request_documents_document_id_version_number",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropIndex(
                name: "IX_authority_request_documents_next_scan_at",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropIndex(
                name: "IX_authority_request_documents_uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_authority_request_documents_current",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_documents_scan",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_documents_version",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "completion_request_reason",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "completion_requested_at",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "completion_requested_at_stage",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "completion_requested_by_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "reviewed_property_updated_at",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "document_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "next_scan_at",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "removed_at",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "row_version",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "scan_attempts",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "scan_detail",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "scan_engine",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "scan_status",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "scanned_at",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "sha256",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "uploaded_by_user_id",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.DropColumn(
                name: "version_number",
                schema: "marketplace",
                table: "authority_request_documents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_independence",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> requester_user_id) AND (issued_by_user_id IS NULL OR (issued_by_user_id <> requester_user_id AND issued_by_user_id <> legal_reviewed_by_user_id)) AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> requester_user_id)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL) AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL) AND (status NOT IN ('Draft', 'PendingLegalReview') OR legal_reviewed_by_user_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_outcomes",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(status = 'Approved') = (issued_by_user_id IS NOT NULL AND issued_at IS NOT NULL AND grant_valid_until IS NOT NULL) AND ((rejected_by_user_id IS NULL AND rejected_at IS NULL AND rejection_reason IS NULL AND rejected_at_stage IS NULL) OR (rejected_by_user_id IS NOT NULL AND rejected_at IS NOT NULL AND rejection_reason IS NOT NULL AND rejected_at_stage IN ('PendingLegalReview', 'PendingIssuance'))) AND (status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_stages",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(status = 'Draft' AND submitted_at IS NULL) OR status = 'Withdrawn' OR (status IN ('PendingLegalReview', 'PendingIssuance', 'Approved') AND submitted_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_status",
                schema: "marketplace",
                table: "authority_requests",
                sql: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance', 'Approved', 'Withdrawn')");

            migrationBuilder.AddForeignKey(
                name: "FK_authority_request_documents_authority_requests_request_id",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "request_id",
                principalSchema: "marketplace",
                principalTable: "authority_requests",
                principalColumn: "id");
        }
    }
}
