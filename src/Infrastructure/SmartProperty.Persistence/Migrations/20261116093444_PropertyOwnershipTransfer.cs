using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PropertyOwnershipTransfer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_marketplace_authority_requests_open",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_kind",
                schema: "marketplace",
                table: "authority_request_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_staff",
                schema: "marketplace",
                table: "authority_request_decisions");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "marketplace",
                table: "authority_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Listing");

            migrationBuilder.AddColumn<Guid>(
                name: "previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "property_ownership_transfers",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_commitment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transferred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_ownership_transfers", x => x.id);
                    table.CheckConstraint("ck_marketplace_ownership_transfers_parties", "(previous_owner_user_id IS NULL OR previous_owner_user_id <> new_owner_user_id) AND issued_by_user_id <> new_owner_user_id AND (previous_owner_user_id IS NULL OR issued_by_user_id <> previous_owner_user_id)");
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_authority_requests_authority_r~",
                        column: x => x.authority_request_id,
                        principalSchema: "marketplace",
                        principalTable: "authority_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_property_commitments_sale_comm~",
                        column: x => x.sale_commitment_id,
                        principalSchema: "marketplace",
                        principalTable: "property_commitments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_users_issued_by_user_id",
                        column: x => x.issued_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_users_new_owner_user_id",
                        column: x => x.new_owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_ownership_transfers_users_previous_owner_user_id",
                        column: x => x.previous_owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "previous_owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_requests_sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "sale_commitment_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_requests_open",
                schema: "marketplace",
                table: "authority_requests",
                column: "property_id",
                unique: true,
                filter: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance') AND kind = 'Listing'");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_requests_open_transfer",
                schema: "marketplace",
                table: "authority_requests",
                columns: new[] { "property_id", "requester_user_id" },
                unique: true,
                filter: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance') AND kind = 'OwnershipTransfer'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_kind",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(kind = 'Listing' AND previous_owner_user_id IS NULL AND sale_commitment_id IS NULL) OR (kind = 'OwnershipTransfer' AND (previous_owner_user_id IS NULL OR previous_owner_user_id <> requester_user_id))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_previous_owner_independence",
                schema: "marketplace",
                table: "authority_requests",
                sql: "previous_owner_user_id IS NULL OR ((legal_reviewed_by_user_id IS NULL OR legal_reviewed_by_user_id <> previous_owner_user_id) AND (issued_by_user_id IS NULL OR issued_by_user_id <> previous_owner_user_id) AND (rejected_by_user_id IS NULL OR rejected_by_user_id <> previous_owner_user_id) AND (completion_requested_by_user_id IS NULL OR completion_requested_by_user_id <> previous_owner_user_id))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_kind",
                schema: "marketplace",
                table: "authority_request_decisions",
                sql: "kind IN ('Submitted', 'LegalApproved', 'LegalCompletionRequested', 'LegalRejected', 'AdminApproved', 'AdminCompletionRequested', 'AdminRejected', 'Withdrawn', 'ClosedOwnershipTransferred')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_staff",
                schema: "marketplace",
                table: "authority_request_decisions",
                sql: "(kind IN ('Submitted', 'Withdrawn', 'ClosedOwnershipTransferred') OR attested_no_conflict) AND (kind NOT IN ('LegalCompletionRequested', 'LegalRejected', 'AdminCompletionRequested', 'AdminRejected', 'ClosedOwnershipTransferred') OR length(btrim(reason)) > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_property_ownership_transfers_issued_by_user_id",
                schema: "marketplace",
                table: "property_ownership_transfers",
                column: "issued_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_property_ownership_transfers_new_owner_user_id",
                schema: "marketplace",
                table: "property_ownership_transfers",
                column: "new_owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_property_ownership_transfers_previous_owner_user_id",
                schema: "marketplace",
                table: "property_ownership_transfers",
                column: "previous_owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_property_ownership_transfers_property_id_transferred_at",
                schema: "marketplace",
                table: "property_ownership_transfers",
                columns: new[] { "property_id", "transferred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_property_ownership_transfers_sale_commitment_id",
                schema: "marketplace",
                table: "property_ownership_transfers",
                column: "sale_commitment_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_ownership_transfers_request",
                schema: "marketplace",
                table: "property_ownership_transfers",
                column: "authority_request_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_authority_requests_property_commitments_sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "sale_commitment_id",
                principalSchema: "marketplace",
                principalTable: "property_commitments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_authority_requests_users_previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests",
                column: "previous_owner_user_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_authority_requests_property_commitments_sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_authority_requests_users_previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropTable(
                name: "property_ownership_transfers",
                schema: "marketplace");

            migrationBuilder.DropIndex(
                name: "IX_authority_requests_previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropIndex(
                name: "IX_authority_requests_sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_authority_requests_open",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_authority_requests_open_transfer",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_kind",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_previous_owner_independence",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_kind",
                schema: "marketplace",
                table: "authority_request_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_staff",
                schema: "marketplace",
                table: "authority_request_decisions");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "previous_owner_user_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.DropColumn(
                name: "sale_commitment_id",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_requests_open",
                schema: "marketplace",
                table: "authority_requests",
                column: "property_id",
                unique: true,
                filter: "status IN ('Draft', 'PendingLegalReview', 'PendingIssuance')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_kind",
                schema: "marketplace",
                table: "authority_request_decisions",
                sql: "kind IN ('Submitted', 'LegalApproved', 'LegalCompletionRequested', 'LegalRejected', 'AdminApproved', 'AdminCompletionRequested', 'AdminRejected', 'Withdrawn')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_request_decisions_staff",
                schema: "marketplace",
                table: "authority_request_decisions",
                sql: "(kind IN ('Submitted', 'Withdrawn') OR attested_no_conflict) AND (kind NOT IN ('LegalCompletionRequested', 'LegalRejected', 'AdminCompletionRequested', 'AdminRejected') OR length(btrim(reason)) > 0)");
        }
    }
}
