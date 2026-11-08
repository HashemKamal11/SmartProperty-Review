using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCountryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code",
                schema: "locations",
                table: "countries",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_locations_countries_code",
                schema: "locations",
                table: "countries",
                sql: "code IS NULL OR code ~ '^[A-Z]{2}$'");

            // The AI service lists countries by name only. A property's address needs the ISO code, so the countries
            // already stored get theirs from the English name; a name not listed here keeps no code (null) rather
            // than a guess, and a property cannot be placed in it until the code is set.
            migrationBuilder.Sql(
                """
                UPDATE locations.countries
                SET code = CASE lower(btrim(name_en))
                    WHEN 'saudi arabia' THEN 'SA'
                    WHEN 'kingdom of saudi arabia' THEN 'SA'
                    WHEN 'egypt' THEN 'EG'
                    WHEN 'arab republic of egypt' THEN 'EG'
                    WHEN 'united arab emirates' THEN 'AE'
                    WHEN 'uae' THEN 'AE'
                    WHEN 'kuwait' THEN 'KW'
                    WHEN 'qatar' THEN 'QA'
                    WHEN 'bahrain' THEN 'BH'
                    WHEN 'oman' THEN 'OM'
                    WHEN 'jordan' THEN 'JO'
                    WHEN 'lebanon' THEN 'LB'
                    WHEN 'iraq' THEN 'IQ'
                    WHEN 'syria' THEN 'SY'
                    WHEN 'yemen' THEN 'YE'
                    WHEN 'palestine' THEN 'PS'
                    WHEN 'libya' THEN 'LY'
                    WHEN 'tunisia' THEN 'TN'
                    WHEN 'algeria' THEN 'DZ'
                    WHEN 'morocco' THEN 'MA'
                    WHEN 'sudan' THEN 'SD'
                    WHEN 'turkey' THEN 'TR'
                    WHEN 'cyprus' THEN 'CY'
                    WHEN 'greece' THEN 'GR'
                    WHEN 'united kingdom' THEN 'GB'
                    WHEN 'united states' THEN 'US'
                    ELSE NULL
                END
                WHERE code IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_locations_countries_code",
                schema: "locations",
                table: "countries");

            migrationBuilder.DropColumn(
                name: "code",
                schema: "locations",
                table: "countries");
        }
    }
}
