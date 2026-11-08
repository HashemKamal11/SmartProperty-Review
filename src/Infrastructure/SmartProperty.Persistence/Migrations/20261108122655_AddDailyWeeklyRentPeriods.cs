using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyWeeklyRentPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((content_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(content_snapshot->'Payload'->'Country') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Country')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Region') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Region')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'City') = 'string' AND length(btrim(content_snapshot->'Payload'->>'City')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'District') = 'string' AND length(btrim(content_snapshot->'Payload'->>'District')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Title') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Title')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Description') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Description')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Currency') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Currency')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3','4','5','6','7','8','9','10','11','12','13','14','15','16') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2','3','4')))), false)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((content_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(content_snapshot->'Payload'->'Country') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Country')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Region') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Region')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'City') = 'string' AND length(btrim(content_snapshot->'Payload'->>'City')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'District') = 'string' AND length(btrim(content_snapshot->'Payload'->>'District')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Title') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Title')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Description') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Description')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Currency') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Currency')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3','4','5','6','7','8','9','10','11','12','13','14','15','16') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2')))), false)");
        }
    }
}
