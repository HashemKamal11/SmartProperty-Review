using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorityRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authority_requests",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requests_sale = table.Column<bool>(type: "boolean", nullable: false),
                    requests_rent = table.Column<bool>(type: "boolean", nullable: false),
                    capacity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ownership_document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    brokerage_license_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    agency_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    property_owner_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sale_listing_content = table.Column<string>(type: "jsonb", nullable: true),
                    rent_listing_content = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legal_reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    legal_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    legal_review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    grant_valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sale_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rent_grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rejected_at_stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_requests", x => x.id);
                    table.CheckConstraint("ck_marketplace_authority_requests_capacity", "(capacity IS NULL AND ownership_document_number IS NULL AND brokerage_license_number IS NULL AND agency_name IS NULL AND property_owner_name IS NULL AND status IN ('Draft', 'Withdrawn')) OR (capacity = 'Owner' AND length(btrim(ownership_document_number)) > 0 AND brokerage_license_number IS NULL AND agency_name IS NULL AND property_owner_name IS NULL) OR (capacity = 'IndividualRepresentative' AND length(btrim(ownership_document_number)) > 0 AND length(btrim(brokerage_license_number)) > 0 AND length(btrim(agency_name)) > 0 AND length(btrim(property_owner_name)) > 0)");
                    table.CheckConstraint("ck_marketplace_authority_requests_grants", "(status = 'Approved') = (sale_grant_id IS NOT NULL OR rent_grant_id IS NOT NULL) AND (status <> 'Approved' OR ((sale_grant_id IS NOT NULL) = requests_sale AND (rent_grant_id IS NOT NULL) = requests_rent))");
                    table.CheckConstraint("ck_marketplace_authority_requests_independence", "(legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> requester_user_id) AND (issued_by_user_id IS NULL OR (issued_by_user_id <> requester_user_id AND issued_by_user_id <> legal_reviewed_by_user_id)) AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> requester_user_id)");
                    table.CheckConstraint("ck_marketplace_authority_requests_legal_review", "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL) AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL) AND (status NOT IN ('Draft', 'PendingLegalReview') OR legal_reviewed_by_user_id IS NULL)");
                    table.CheckConstraint("ck_marketplace_authority_requests_listings", "(requests_sale OR sale_listing_content IS NULL) AND (requests_rent OR rent_listing_content IS NULL) AND (status IN ('Draft', 'Withdrawn') OR ((NOT requests_sale OR sale_listing_content IS NOT NULL) AND (NOT requests_rent OR rent_listing_content IS NOT NULL)))");
                    table.CheckConstraint("ck_marketplace_authority_requests_outcomes", "(status = 'Approved') = (issued_by_user_id IS NOT NULL AND issued_at IS NOT NULL AND grant_valid_until IS NOT NULL) AND ((rejected_by_user_id IS NULL AND rejected_at IS NULL AND rejection_reason IS NULL AND rejected_at_stage IS NULL) OR (rejected_by_user_id IS NOT NULL AND rejected_at IS NOT NULL AND rejection_reason IS NOT NULL AND rejected_at_stage IN ('PendingLegalReview', 'PendingIssuance'))) AND (status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
                    table.CheckConstraint("ck_marketplace_authority_requests_purposes", "requests_sale OR requests_rent");
                    table.CheckConstraint("ck_marketplace_authority_requests_stages", "(status = 'Draft' AND submitted_at IS NULL) OR status = 'Withdrawn' OR (status IN ('PendingLegalReview', 'PendingIssuance', 'Approved') AND submitted_at IS NOT NULL)");
                    table.CheckConstraint("ck_marketplace_authority_requests_status", "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance', 'Approved', 'Withdrawn')");
                    table.ForeignKey(
                        name: "FK_authority_requests_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authority_requests_users_issued_by_user_id",
                        column: x => x.issued_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authority_requests_users_legal_reviewed_by_user_id",
                        column: x => x.legal_reviewed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authority_requests_users_rejected_by_user_id",
                        column: x => x.rejected_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_authority_requests_users_requester_user_id",
                        column: x => x.requester_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "authority_request_documents",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authority_request_documents", x => x.id);
                    table.CheckConstraint("ck_marketplace_authority_request_documents_content", "content_type IN ('image/jpeg', 'image/png', 'image/webp', 'application/pdf') AND size_bytes > 0 AND size_bytes <= 10485760");
                    table.CheckConstraint("ck_marketplace_authority_request_documents_type", "document_type IN ('TitleDeed', 'SaleContract', 'UtilityBill', 'Other', 'Authorization', 'BrokerageLicense')");
                    table.ForeignKey(
                        name: "FK_authority_request_documents_authority_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "marketplace",
                        principalTable: "authority_requests",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_documents_request_id",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_request_documents_storage_key",
                schema: "marketplace",
                table: "authority_request_documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_issued_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "issued_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_legal_reviewed_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "legal_reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_rejected_by_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "rejected_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_requester_user_id_created_at",
                schema: "marketplace",
                table: "authority_requests",
                columns: new[] { "requester_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_status_submitted_at",
                schema: "marketplace",
                table: "authority_requests",
                columns: new[] { "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_requests_open",
                schema: "marketplace",
                table: "authority_requests",
                column: "property_id",
                unique: true,
                filter: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authority_request_documents",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "authority_requests",
                schema: "marketplace");
        }
    }
}
