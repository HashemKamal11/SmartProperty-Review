using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalPricingOptionsAndCountryTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                schema: "locations",
                table: "countries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // A rental is dated in the property's local time. Countries that were copied before this column existed
            // get the one zone their code has; a country with several zones, or none recorded, stays null and a
            // rental in it is refused instead of being dated by a guess. This list is CountryTimeZones.
            migrationBuilder.Sql("""
                UPDATE locations.countries SET time_zone_id = CASE code
                    WHEN 'EG' THEN 'Africa/Cairo'
                    WHEN 'SA' THEN 'Asia/Riyadh'
                    WHEN 'AE' THEN 'Asia/Dubai'
                    WHEN 'KW' THEN 'Asia/Kuwait'
                    WHEN 'QA' THEN 'Asia/Qatar'
                    WHEN 'BH' THEN 'Asia/Bahrain'
                    WHEN 'OM' THEN 'Asia/Muscat'
                    WHEN 'JO' THEN 'Asia/Amman'
                    WHEN 'LB' THEN 'Asia/Beirut'
                    WHEN 'IQ' THEN 'Asia/Baghdad'
                    WHEN 'LY' THEN 'Africa/Tripoli'
                    WHEN 'TN' THEN 'Africa/Tunis'
                    WHEN 'DZ' THEN 'Africa/Algiers'
                    WHEN 'MA' THEN 'Africa/Casablanca'
                END
                WHERE code IN ('EG','SA','AE','KW','QA','BH','OM','JO','LB','IQ','LY','TN','DZ','MA');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_rent_terms",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE(((NOT jsonb_exists(content_snapshot->'Payload','PricingOptions') AND NOT jsonb_exists(content_snapshot->'Payload','RentalTerms')) OR (purpose = 'Rent' AND jsonb_typeof(content_snapshot->'Payload'->'PricingOptions') = 'array' AND jsonb_array_length(content_snapshot->'Payload'->'PricingOptions') BETWEEN 1 AND 4 AND jsonb_typeof(content_snapshot->'Payload'->'RentalTerms') = 'object' AND jsonb_typeof(content_snapshot->'Payload'->'RentalTerms'->'Windows') = 'array' AND jsonb_array_length(content_snapshot->'Payload'->'RentalTerms'->'Windows') >= 1)), false)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_rent_terms",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                schema: "locations",
                table: "countries");
        }
    }
}
