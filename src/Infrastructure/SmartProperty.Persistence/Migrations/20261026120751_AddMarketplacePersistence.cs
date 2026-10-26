using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplacePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "marketplace");

            migrationBuilder.CreateTable(
                name: "listings",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    publication_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    availability_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    published_submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_reservation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listings", x => x.id);
                    table.UniqueConstraint("ak_marketplace_listings_identity", x => new { x.id, x.property_id, x.purpose });
                    table.CheckConstraint("ck_marketplace_listings_history", "updated_at >= created_at AND ((publication_status = 'Unpublished' AND published_submission_id IS NULL AND published_at IS NULL AND withdrawn_at IS NULL) OR (publication_status = 'Published' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND published_at >= created_at AND published_at <= updated_at AND (withdrawn_at IS NULL OR (withdrawn_at >= created_at AND withdrawn_at <= published_at))) OR (publication_status = 'Withdrawn' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND withdrawn_at IS NOT NULL AND published_at >= created_at AND withdrawn_at >= published_at AND withdrawn_at <= updated_at))");
                    table.CheckConstraint("ck_marketplace_listings_ids", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND (published_submission_id IS NULL OR published_submission_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("ck_marketplace_listings_reservation_closure", "(NOT closed_by_reservation OR availability_status IN ('Closed','Reserved')) AND (availability_status <> 'Reserved' OR closed_by_reservation)");
                    table.CheckConstraint("ck_marketplace_listings_state", "purpose IN ('Sale', 'Rent') AND ((publication_status = 'Unpublished' AND availability_status IN ('Open', 'Closed')) OR (publication_status = 'Published' AND availability_status = 'Open') OR (publication_status = 'Withdrawn' AND availability_status IN ('Reserved', 'Closed')))");
                    table.ForeignKey(
                        name: "fk_marketplace_listings_property",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_requests",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    listing_purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_purchase_requests", x => x.id);
                    table.UniqueConstraint("ak_marketplace_purchase_requests_identity", x => new { x.id, x.listing_id, x.property_id });
                    table.CheckConstraint("ck_marketplace_purchase_requests_history", "updated_at >= submitted_at AND ((status = 'Submitted' AND decided_at IS NULL) OR (status <> 'Submitted' AND decided_at IS NOT NULL AND decided_at >= submitted_at AND decided_at = updated_at))");
                    table.CheckConstraint("ck_marketplace_purchase_requests_ids", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND buyer_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_purchase_requests_state", "listing_purpose = 'Sale' AND status IN ('Submitted', 'Accepted', 'Rejected', 'WithdrawnByRequester', 'ClosedListingUnavailable')");
                    table.ForeignKey(
                        name: "fk_marketplace_purchase_requests_buyer",
                        column: x => x.buyer_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_purchase_requests_listing",
                        columns: x => new { x.listing_id, x.property_id, x.listing_purpose },
                        principalSchema: "marketplace",
                        principalTable: "listings",
                        principalColumns: new[] { "id", "property_id", "purpose" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rental_requests",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    renter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    listing_purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_rental_requests", x => x.id);
                    table.UniqueConstraint("ak_marketplace_rental_requests_identity", x => new { x.id, x.listing_id, x.property_id });
                    table.CheckConstraint("ck_marketplace_rental_requests_history", "updated_at >= submitted_at AND ((status = 'Submitted' AND decided_at IS NULL) OR (status <> 'Submitted' AND decided_at IS NOT NULL AND decided_at >= submitted_at AND decided_at = updated_at))");
                    table.CheckConstraint("ck_marketplace_rental_requests_ids", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND renter_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_rental_requests_state", "listing_purpose = 'Rent' AND status IN ('Submitted', 'Accepted', 'Rejected', 'WithdrawnByRequester', 'ClosedListingUnavailable')");
                    table.ForeignKey(
                        name: "fk_marketplace_rental_requests_listing",
                        columns: x => new { x.listing_id, x.property_id, x.listing_purpose },
                        principalSchema: "marketplace",
                        principalTable: "listings",
                        principalColumns: new[] { "id", "property_id", "purpose" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_rental_requests_renter",
                        column: x => x.renter_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submissions",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    predecessor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    authority_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    evidence_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    submitted_assessments_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_submissions", x => x.id);
                    table.UniqueConstraint("ak_marketplace_submissions_identity", x => new { x.id, x.listing_id, x.property_id, x.purpose });
                    table.CheckConstraint("ck_marketplace_submissions_content", "COALESCE((content_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(content_snapshot->'Payload'->'Country') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Country')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Region') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Region')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'City') = 'string' AND length(btrim(content_snapshot->'Payload'->>'City')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'District') = 'string' AND length(btrim(content_snapshot->'Payload'->>'District')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Title') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Title')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Description') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Description')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Currency') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Currency')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2')))), false)");
                    table.CheckConstraint("ck_marketplace_submissions_evidence", "COALESCE((jsonb_typeof(evidence_snapshot->'Payload') = 'array' AND jsonb_array_length(evidence_snapshot->'Payload') > 0 AND evidence_snapshot->>'FormatVersion' = '1' AND authority_snapshot->>'FormatVersion' = '1' AND submitted_assessments_snapshot->>'FormatVersion' = '1'), false)");
                    table.CheckConstraint("ck_marketplace_submissions_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND version > 0 AND ((version = 1 AND predecessor_id IS NULL) OR (version > 1 AND predecessor_id IS NOT NULL AND predecessor_id <> id))");
                    table.CheckConstraint("ck_marketplace_submissions_state", "purpose IN ('Sale','Rent') AND status IN ('Draft','PendingReview','Approved','Rejected','Superseded') AND updated_at >= created_at AND ((status = 'Draft' AND submitted_at IS NULL) OR (status <> 'Draft' AND submitted_at IS NOT NULL AND submitted_at >= created_at AND submitted_at <= updated_at))");
                    table.ForeignKey(
                        name: "fk_marketplace_submissions_listing",
                        columns: x => new { x.listing_id, x.property_id, x.purpose },
                        principalSchema: "marketplace",
                        principalTable: "listings",
                        principalColumns: new[] { "id", "property_id", "purpose" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_submissions_predecessor",
                        columns: x => new { x.predecessor_id, x.listing_id, x.property_id, x.purpose },
                        principalSchema: "marketplace",
                        principalTable: "submissions",
                        principalColumns: new[] { "id", "listing_id", "property_id", "purpose" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "property_reservations",
                schema: "marketplace",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reserved_purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    winning_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    winning_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purchase_winner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rental_winner_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_property_reservations", x => x.property_id);
                    table.CheckConstraint("ck_marketplace_property_reservations_ids", "property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND winning_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND winning_request_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_property_reservations_purpose", "reserved_purpose IN ('Sale', 'Rent')");
                    table.CheckConstraint("ck_marketplace_property_reservations_winner", "(reserved_purpose = 'Sale' AND purchase_winner_id IS NOT NULL AND purchase_winner_id = winning_request_id AND rental_winner_id IS NULL) OR (reserved_purpose = 'Rent' AND rental_winner_id IS NOT NULL AND rental_winner_id = winning_request_id AND purchase_winner_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_marketplace_property_reservations_listing",
                        columns: x => new { x.winning_listing_id, x.property_id, x.reserved_purpose },
                        principalSchema: "marketplace",
                        principalTable: "listings",
                        principalColumns: new[] { "id", "property_id", "purpose" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_property_reservations_property",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_reservations_purchase_winner",
                        columns: x => new { x.purchase_winner_id, x.winning_listing_id, x.property_id },
                        principalSchema: "marketplace",
                        principalTable: "purchase_requests",
                        principalColumns: new[] { "id", "listing_id", "property_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_reservations_rental_winner",
                        columns: x => new { x.rental_winner_id, x.winning_listing_id, x.property_id },
                        principalSchema: "marketplace",
                        principalTable: "rental_requests",
                        principalColumns: new[] { "id", "listing_id", "property_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assessment_snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_review_decisions", x => x.id);
                    table.CheckConstraint("ck_marketplace_decisions_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_decisions_reason", "approved OR (reason IS NOT NULL AND length(btrim(reason)) > 0)");
                    table.CheckConstraint("ck_marketplace_decisions_snapshots", "COALESCE((reviewer_snapshot->>'FormatVersion' = '1' AND assessment_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(assessment_snapshot->'Payload') = 'array'), false)");
                    table.ForeignKey(
                        name: "fk_marketplace_decisions_submission",
                        column: x => x.submission_id,
                        principalSchema: "marketplace",
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_listings_published_submission_id_id_property_id_purpose",
                schema: "marketplace",
                table: "listings",
                columns: new[] { "published_submission_id", "id", "property_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_listings_property_purpose",
                schema: "marketplace",
                table: "listings",
                columns: new[] { "property_id", "purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_reservations_purchase_winner_id_winning_listing_id~",
                schema: "marketplace",
                table: "property_reservations",
                columns: new[] { "purchase_winner_id", "winning_listing_id", "property_id" });

            migrationBuilder.CreateIndex(
                name: "IX_property_reservations_rental_winner_id_winning_listing_id_p~",
                schema: "marketplace",
                table: "property_reservations",
                columns: new[] { "rental_winner_id", "winning_listing_id", "property_id" });

            migrationBuilder.CreateIndex(
                name: "IX_property_reservations_winning_listing_id_property_id_reserv~",
                schema: "marketplace",
                table: "property_reservations",
                columns: new[] { "winning_listing_id", "property_id", "reserved_purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_purchase_requests_property_status",
                schema: "marketplace",
                table: "purchase_requests",
                columns: new[] { "property_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requests_buyer_user_id",
                schema: "marketplace",
                table: "purchase_requests",
                column: "buyer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requests_listing_id_property_id_listing_purpose",
                schema: "marketplace",
                table: "purchase_requests",
                columns: new[] { "listing_id", "property_id", "listing_purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_rental_requests_property_status",
                schema: "marketplace",
                table: "rental_requests",
                columns: new[] { "property_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_requests_listing_id_property_id_listing_purpose",
                schema: "marketplace",
                table: "rental_requests",
                columns: new[] { "listing_id", "property_id", "listing_purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_requests_renter_user_id",
                schema: "marketplace",
                table: "rental_requests",
                column: "renter_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_decisions_submission",
                schema: "marketplace",
                table: "review_decisions",
                column: "submission_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_submissions_listing_id_property_id_purpose",
                schema: "marketplace",
                table: "submissions",
                columns: new[] { "listing_id", "property_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_submissions_predecessor_id_listing_id_property_id_purpose",
                schema: "marketplace",
                table: "submissions",
                columns: new[] { "predecessor_id", "listing_id", "property_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_submissions_listing_version",
                schema: "marketplace",
                table: "submissions",
                columns: new[] { "listing_id", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_marketplace_listings_published_submission",
                schema: "marketplace",
                table: "listings",
                columns: new[] { "published_submission_id", "id", "property_id", "purpose" },
                principalSchema: "marketplace",
                principalTable: "submissions",
                principalColumns: new[] { "id", "listing_id", "property_id", "purpose" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_marketplace_listings_published_submission",
                schema: "marketplace",
                table: "listings");

            migrationBuilder.DropTable(
                name: "property_reservations",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "review_decisions",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "purchase_requests",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "rental_requests",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "submissions",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "listings",
                schema: "marketplace");
        }
    }
}
