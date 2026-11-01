using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddListingSubmissionRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_evidence",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                schema: "marketplace",
                table: "submissions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // The constant default initializes existing rows without an UPDATE of their snapshots.
            // Remove it immediately: future inserts must supply the Domain/EF revision explicitly.
            migrationBuilder.Sql("ALTER TABLE marketplace.submissions ALTER COLUMN revision DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_evidence",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((jsonb_typeof(evidence_snapshot->'Payload') = 'array' AND (status = 'Draft' OR jsonb_array_length(evidence_snapshot->'Payload') > 0) AND evidence_snapshot->>'FormatVersion' = '1' AND authority_snapshot->>'FormatVersion' = '1' AND submitted_assessments_snapshot->>'FormatVersion' = '1'), false)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_revision",
                schema: "marketplace",
                table: "submissions",
                sql: "revision > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_evidence",
                schema: "marketplace",
                table: "submissions");

            // Validate the previous rule before removing revision metadata. An empty-evidence
            // draft must fail rollback, not be rewritten or deleted. Keep all operations transactional.
            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_evidence",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((jsonb_typeof(evidence_snapshot->'Payload') = 'array' AND jsonb_array_length(evidence_snapshot->'Payload') > 0 AND evidence_snapshot->>'FormatVersion' = '1' AND authority_snapshot->>'FormatVersion' = '1' AND submitted_assessments_snapshot->>'FormatVersion' = '1'), false)");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_revision",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.DropColumn(
                name: "revision",
                schema: "marketplace",
                table: "submissions");
        }
    }
}
