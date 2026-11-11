using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestDetailsAndCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rental_requests_renter_user_id",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requests_buyer_user_id",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.AddColumn<decimal>(
                name: "amount",
                schema: "marketplace",
                table: "rental_requests",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "check_in_time",
                schema: "marketplace",
                table: "rental_requests",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "check_out_time",
                schema: "marketplace",
                table: "rental_requests",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "marketplace",
                table: "rental_requests",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "end_date",
                schema: "marketplace",
                table: "rental_requests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "message",
                schema: "marketplace",
                table: "rental_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pricing_period",
                schema: "marketplace",
                table: "rental_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "start_date",
                schema: "marketplace",
                table: "rental_requests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "submission_id",
                schema: "marketplace",
                table: "rental_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                schema: "marketplace",
                table: "rental_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "marketplace",
                table: "purchase_requests",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "message",
                schema: "marketplace",
                table: "purchase_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "offered_amount",
                schema: "marketplace",
                table: "purchase_requests",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "submission_id",
                schema: "marketplace",
                table: "purchase_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "property_commitments",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rental_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pricing_period = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    check_in_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    check_out_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signing_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handed_over_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_property_commitments", x => x.id);
                    table.CheckConstraint("ck_marketplace_commitments_history", "signing_deadline > accepted_at AND updated_at >= accepted_at AND ((status = 'PendingSignature' AND signed_at IS NULL AND handed_over_at IS NULL AND completed_at IS NULL AND expired_at IS NULL) OR (status = 'Signed' AND signed_at IS NOT NULL AND handed_over_at IS NULL AND completed_at IS NULL AND expired_at IS NULL) OR (status = 'Occupied' AND kind = 'Rental' AND signed_at IS NOT NULL AND handed_over_at IS NOT NULL AND completed_at IS NULL AND expired_at IS NULL) OR (status = 'Completed' AND signed_at IS NOT NULL AND completed_at IS NOT NULL AND expired_at IS NULL) OR (status = 'Expired' AND expired_at IS NOT NULL AND signed_at IS NULL AND handed_over_at IS NULL AND completed_at IS NULL))");
                    table.CheckConstraint("ck_marketplace_commitments_money", "amount > 0 AND currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_marketplace_commitments_shape", "(kind = 'Sale' AND purchase_request_id IS NOT NULL AND rental_request_id IS NULL AND pricing_period IS NULL AND start_date IS NULL AND end_date IS NULL AND check_in_time IS NULL AND check_out_time IS NULL AND time_zone_id IS NULL) OR (kind = 'Rental' AND rental_request_id IS NOT NULL AND purchase_request_id IS NULL AND pricing_period IN ('Daily', 'Weekly', 'Monthly', 'Yearly') AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date > start_date AND check_in_time IS NOT NULL AND check_out_time IS NOT NULL AND time_zone_id IS NOT NULL)");
                    table.CheckConstraint("ck_marketplace_commitments_state", "kind IN ('Sale', 'Rental') AND status IN ('PendingSignature', 'Signed', 'Occupied', 'Completed', 'Expired')");
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_customer",
                        column: x => x.customer_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_listing",
                        column: x => x.listing_id,
                        principalSchema: "marketplace",
                        principalTable: "listings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_owner",
                        column: x => x.owner_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_property",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_purchase_request",
                        column: x => x.purchase_request_id,
                        principalSchema: "marketplace",
                        principalTable: "purchase_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_rental_request",
                        column: x => x.rental_request_id,
                        principalSchema: "marketplace",
                        principalTable: "rental_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_commitments_submission",
                        column: x => x.submission_id,
                        principalSchema: "marketplace",
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    signing_window_hours = table.Column<int>(type: "integer", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_settings", x => x.id);
                    table.CheckConstraint("ck_marketplace_settings_signing_window", "signing_window_hours BETWEEN 1 AND 720");
                    table.CheckConstraint("ck_marketplace_settings_singleton", "id = 1");
                    table.ForeignKey(
                        name: "fk_marketplace_settings_updated_by",
                        column: x => x.updated_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_rental_requests_renter",
                schema: "marketplace",
                table: "rental_requests",
                columns: new[] { "renter_user_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_requests_submission_id",
                schema: "marketplace",
                table: "rental_requests",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_rental_requests_open",
                schema: "marketplace",
                table: "rental_requests",
                columns: new[] { "listing_id", "renter_user_id" },
                unique: true,
                filter: "status = 'Submitted'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_rental_requests_terms",
                schema: "marketplace",
                table: "rental_requests",
                sql: "(submission_id IS NULL AND pricing_period IS NULL AND amount IS NULL AND currency IS NULL AND start_date IS NULL AND end_date IS NULL AND check_in_time IS NULL AND check_out_time IS NULL AND time_zone_id IS NULL) OR (submission_id IS NOT NULL AND pricing_period IN ('Daily', 'Weekly', 'Monthly', 'Yearly') AND amount > 0 AND currency ~ '^[A-Z]{3}$' AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date > start_date AND check_in_time IS NOT NULL AND check_out_time IS NOT NULL AND time_zone_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_purchase_requests_buyer",
                schema: "marketplace",
                table: "purchase_requests",
                columns: new[] { "buyer_user_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requests_submission_id",
                schema: "marketplace",
                table: "purchase_requests",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_purchase_requests_open",
                schema: "marketplace",
                table: "purchase_requests",
                columns: new[] { "listing_id", "buyer_user_id" },
                unique: true,
                filter: "status = 'Submitted'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_purchase_requests_terms",
                schema: "marketplace",
                table: "purchase_requests",
                sql: "(submission_id IS NULL AND offered_amount IS NULL AND currency IS NULL) OR (submission_id IS NOT NULL AND offered_amount > 0 AND currency ~ '^[A-Z]{3}$')");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_commitments_customer",
                schema: "marketplace",
                table: "property_commitments",
                columns: new[] { "customer_user_id", "accepted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_commitments_owner",
                schema: "marketplace",
                table: "property_commitments",
                columns: new[] { "owner_user_id", "accepted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_commitments_property_status",
                schema: "marketplace",
                table: "property_commitments",
                columns: new[] { "property_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_property_commitments_listing_id",
                schema: "marketplace",
                table: "property_commitments",
                column: "listing_id");

            migrationBuilder.CreateIndex(
                name: "IX_property_commitments_submission_id",
                schema: "marketplace",
                table: "property_commitments",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_commitments_purchase_request",
                schema: "marketplace",
                table: "property_commitments",
                column: "purchase_request_id",
                unique: true,
                filter: "purchase_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_commitments_rental_request",
                schema: "marketplace",
                table: "property_commitments",
                column: "rental_request_id",
                unique: true,
                filter: "rental_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_settings_updated_by_user_id",
                schema: "marketplace",
                table: "settings",
                column: "updated_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_marketplace_purchase_requests_submission",
                schema: "marketplace",
                table: "purchase_requests",
                column: "submission_id",
                principalSchema: "marketplace",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_marketplace_rental_requests_submission",
                schema: "marketplace",
                table: "rental_requests",
                column: "submission_id",
                principalSchema: "marketplace",
                principalTable: "submissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Two rules the database enforces by itself, so no sequence of concurrent accepts can break them even if a
            // handler's own check is wrong or skipped. EF cannot model exclusion constraints, so they are plain SQL.
            //
            // They are DEFERRABLE INITIALLY DEFERRED: the check happens at commit, which lets one unit of work expire an
            // overdue unsigned commitment and commit its replacement together, whatever order the statements run in.
            //
            // btree_gist is a trusted extension (PostgreSQL 13+), so the database owner may create it. It is not dropped
            // on rollback because other objects may come to depend on it.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            // The stay is the half-open range [start, end): a stay may begin on the day another one ends.
            migrationBuilder.Sql(
                """
                ALTER TABLE marketplace.property_commitments
                ADD CONSTRAINT ex_marketplace_commitments_rental_overlap
                EXCLUDE USING gist (property_id WITH =, daterange(start_date, end_date, '[)') WITH &&)
                WHERE (kind = 'Rental' AND status IN ('PendingSignature', 'Signed', 'Occupied'))
                DEFERRABLE INITIALLY DEFERRED;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE marketplace.property_commitments
                ADD CONSTRAINT ex_marketplace_commitments_single_sale
                EXCLUDE USING gist (property_id WITH =)
                WHERE (kind = 'Sale' AND status IN ('PendingSignature', 'Signed', 'Occupied'))
                DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_marketplace_purchase_requests_submission",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_marketplace_rental_requests_submission",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropTable(
                name: "property_commitments",
                schema: "marketplace");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "marketplace");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_rental_requests_renter",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropIndex(
                name: "IX_rental_requests_submission_id",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_rental_requests_open",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_rental_requests_terms",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_purchase_requests_buyer",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropIndex(
                name: "IX_purchase_requests_submission_id",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_purchase_requests_open",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_purchase_requests_terms",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "amount",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "check_in_time",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "check_out_time",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "end_date",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "message",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "pricing_period",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "start_date",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "submission_id",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                schema: "marketplace",
                table: "rental_requests");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "message",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "offered_amount",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.DropColumn(
                name: "submission_id",
                schema: "marketplace",
                table: "purchase_requests");

            migrationBuilder.CreateIndex(
                name: "IX_rental_requests_renter_user_id",
                schema: "marketplace",
                table: "rental_requests",
                column: "renter_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_requests_buyer_user_id",
                schema: "marketplace",
                table: "purchase_requests",
                column: "buyer_user_id");
        }
    }
}
