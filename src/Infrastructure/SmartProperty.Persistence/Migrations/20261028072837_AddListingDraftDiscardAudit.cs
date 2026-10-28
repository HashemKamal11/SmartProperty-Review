using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListingDraftDiscardAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "draft_discard_audits",
                schema: "marketplace",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    listing_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_discard_audits", x => x.event_id);
                    table.CheckConstraint("ck_marketplace_discard_audits_identity", "event_id <> '00000000-0000-0000-0000-000000000000'::uuid AND actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND submission_id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_discard_audits_values", "purpose IN ('Sale','Rent') AND version > 0 AND revision > 0 AND isfinite(occurred_at) AND occurred_at <> '0001-01-01 00:00:00+00'::timestamptz");
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_discard_audits_property_time",
                schema: "marketplace",
                table: "draft_discard_audits",
                columns: new[] { "property_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_discard_audits_submission",
                schema: "marketplace",
                table: "draft_discard_audits",
                column: "submission_id",
                unique: true);

            // Database enforcement complements EF save guards; it does not authorize an insert.
            migrationBuilder.Sql("""
                CREATE FUNCTION marketplace.reject_discard_audit_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $audit$
                BEGIN
                    RAISE EXCEPTION 'Draft discard audit history is append-only.'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_marketplace_discard_audits_append_only';
                    RETURN NULL;
                END;
                $audit$;
                CREATE TRIGGER tr_marketplace_discard_audits_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON marketplace.draft_discard_audits
                FOR EACH STATEMENT EXECUTE FUNCTION marketplace.reject_discard_audit_mutation();
                ALTER TABLE marketplace.draft_discard_audits
                    ENABLE ALWAYS TRIGGER tr_marketplace_discard_audits_append_only;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lock before checking to prevent an insert racing the emptiness check. Never erase history.
            migrationBuilder.Sql("""
                LOCK TABLE marketplace.draft_discard_audits IN ACCESS EXCLUSIVE MODE;
                DO $audit$
                BEGIN
                    IF EXISTS (SELECT 1 FROM marketplace.draft_discard_audits) THEN
                        RAISE EXCEPTION 'Cannot roll back a nonempty discard audit table.'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $audit$;
                """);
            migrationBuilder.DropTable(
                name: "draft_discard_audits",
                schema: "marketplace");
            migrationBuilder.Sql("DROP FUNCTION marketplace.reject_discard_audit_mutation();");
        }
    }
}
