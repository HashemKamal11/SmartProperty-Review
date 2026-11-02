using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <summary>
    /// Adopts the AI search service vocabulary and adds what a property needs to be sent to it.
    /// </summary>
    /// <remarks>
    /// Written by hand because the order matters. The old constraints are dropped first, existing rows are
    /// converted to the new vocabulary, and only then are the new constraints added, so no existing row is ever
    /// rejected by a constraint it was not written under.
    ///
    /// Conversion of existing properties:
    ///   type   Unit            -> Apartment
    ///   finish Unfinished      -> CoreAndShell
    ///          FullyFinished   -> Finished
    ///          SuperLux        -> LuxuryFinished
    ///          Furnished       -> Finished, with furnished = true
    ///          SemiFinished    -> unchanged
    ///
    /// The marketplace stores a property type as its number inside listing snapshots; 1, 2, and 3 keep their
    /// meaning (3 was Unit and is now Apartment), so no marketplace data changes. Only its check constraint is widened.
    /// </remarks>
    public partial class AddAiSearchIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Drop the constraints the new vocabulary replaces.
            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_type",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions");

            // 2. New columns.
            migrationBuilder.AddColumn<int>(
                name: "ai_area_id",
                schema: "registry",
                table: "properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ai_compound_id",
                schema: "registry",
                table: "properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_sync_error",
                schema: "registry",
                table: "properties",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_sync_status",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotSynced");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_synced_at",
                schema: "registry",
                table: "properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "area_sqm",
                schema: "registry",
                table: "properties",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description_ar",
                schema: "registry",
                table: "properties",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description_en",
                schema: "registry",
                table: "properties",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "floor",
                schema: "registry",
                table: "properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "furnished",
                schema: "registry",
                table: "properties",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "listing_status",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<decimal>(
                name: "price",
                schema: "registry",
                table: "properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title_ar",
                schema: "registry",
                table: "properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title_en",
                schema: "registry",
                table: "properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_type",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);


            // 3. Convert existing rows, while no constraint can object.
            migrationBuilder.Sql(
                """
                UPDATE registry.properties SET type = 'Apartment' WHERE type = 'Unit';

                UPDATE registry.properties SET furnished = TRUE WHERE finishing_status = 'Furnished';

                UPDATE registry.properties
                SET finishing_status = CASE finishing_status
                    WHEN 'Unfinished' THEN 'CoreAndShell'
                    WHEN 'FullyFinished' THEN 'Finished'
                    WHEN 'SuperLux' THEN 'LuxuryFinished'
                    WHEN 'Furnished' THEN 'Finished'
                    ELSE finishing_status
                END
                WHERE finishing_status IN ('Unfinished', 'FullyFinished', 'SuperLux', 'Furnished');
                """);

            // 4. The constraints for the new vocabulary and the new columns.
            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_type",
                schema: "registry",
                table: "properties",
                sql: "type IN ('Land', 'Building', 'Apartment', 'Villa', 'Duplex', 'Studio', 'Chalet', 'Townhouse', 'TwinHouse', 'Penthouse', 'StandaloneVilla', 'HotelApartment', 'Office', 'Shop', 'Warehouse', 'Farm')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties",
                sql: "finishing_status IS NULL OR finishing_status IN ('Finished', 'LuxuryFinished', 'SemiFinished', 'CoreAndShell')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_ai_place",
                schema: "registry",
                table: "properties",
                sql: "(ai_area_id IS NULL OR ai_area_id > 0) AND (ai_compound_id IS NULL OR (ai_compound_id > 0 AND ai_area_id IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_ai_sync_status",
                schema: "registry",
                table: "properties",
                sql: "ai_sync_status IN ('NotSynced', 'Synced', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_area_sqm_range",
                schema: "registry",
                table: "properties",
                sql: "area_sqm IS NULL OR (area_sqm > 0 AND area_sqm <= 100000)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_floor_range",
                schema: "registry",
                table: "properties",
                sql: "floor IS NULL OR floor BETWEEN -5 AND 300");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_listing_status",
                schema: "registry",
                table: "properties",
                sql: "listing_status IN ('Available', 'Reserved', 'Sold', 'Rented')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_price_positive",
                schema: "registry",
                table: "properties",
                sql: "price IS NULL OR price > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_transaction_type",
                schema: "registry",
                table: "properties",
                sql: "transaction_type IS NULL OR transaction_type IN ('Sale', 'Rent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((content_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(content_snapshot->'Payload'->'Country') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Country')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Region') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Region')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'City') = 'string' AND length(btrim(content_snapshot->'Payload'->>'City')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'District') = 'string' AND length(btrim(content_snapshot->'Payload'->>'District')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Title') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Title')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Description') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Description')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Currency') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Currency')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3','4','5','6','7','8','9','10','11','12','13','14','15','16') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2')))), false)");

            // 5. Amenities: the AI service's code for each one that has an equivalent.
            migrationBuilder.AddColumn<string>(
                name: "ai_code",
                schema: "registry",
                table: "amenities",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // 6. Views, and the link between a property and its views.
            migrationBuilder.CreateTable(
                name: "views",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name_ar = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name_en = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_views", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "property_views",
                schema: "registry",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    view_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_views", x => new { x.property_id, x.view_id });
                    table.ForeignKey(
                        name: "FK_property_views_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_views_views_view_id",
                        column: x => x.view_id,
                        principalSchema: "registry",
                        principalTable: "views",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_registry_views_code",
                schema: "registry",
                table: "views",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_views_view_id",
                schema: "registry",
                table: "property_views",
                column: "view_id");

            // 7. Seed data. The views are the AI service's own list (GET /v1/options).
            migrationBuilder.Sql(
                """
                INSERT INTO registry.views (id, code, name_ar, name_en, sort_order) VALUES
                    (gen_random_uuid(), 'City', 'مدينة', 'City', 1),
                    (gen_random_uuid(), 'Community', 'كومباوند', 'Community', 2),
                    (gen_random_uuid(), 'Garden', 'حديقة', 'Garden', 3),
                    (gen_random_uuid(), 'Golf', 'جولف', 'Golf', 4),
                    (gen_random_uuid(), 'Nile', 'نيل', 'Nile', 5),
                    (gen_random_uuid(), 'Pool', 'حمام سباحة', 'Pool', 6),
                    (gen_random_uuid(), 'Sea', 'بحر', 'Sea', 7),
                    (gen_random_uuid(), 'Street', 'شارع', 'Street', 8)
                ON CONFLICT (code) DO NOTHING;
                """);

            // The AI codes of the amenities this platform already had. Several can share one code: a private and a
            // shared gym are both "gym".
            migrationBuilder.Sql(
                """
                UPDATE registry.amenities SET ai_code = 'balcony' WHERE code = 'Balcony';
                UPDATE registry.amenities SET ai_code = 'security' WHERE code = 'Security';
                UPDATE registry.amenities SET ai_code = 'central ac' WHERE code = 'CentralAirConditioning';
                UPDATE registry.amenities SET ai_code = 'pool' WHERE code = 'SwimmingPool';
                UPDATE registry.amenities SET ai_code = 'pool' WHERE code = 'PrivatePool';
                UPDATE registry.amenities SET ai_code = 'gym' WHERE code = 'PrivateGym';
                UPDATE registry.amenities SET ai_code = 'gym' WHERE code = 'SharedGym';
                UPDATE registry.amenities SET ai_code = 'kids area' WHERE code = 'ChildrensPlayArea';
                UPDATE registry.amenities SET ai_code = 'parking' WHERE code = 'CoveredParking';
                UPDATE registry.amenities SET ai_code = 'concierge' WHERE code = 'ReceptionService';
                """);

            // The AI service's amenities this platform lacked, so a property can state them.
            migrationBuilder.Sql(
                """
                INSERT INTO registry.amenities (id, code, name_ar, name_en, sort_order, ai_code) VALUES
                    (gen_random_uuid(), 'Garden', 'حديقة', 'Garden', 30, 'garden'),
                    (gen_random_uuid(), 'Elevator', 'مصعد', 'Elevator', 31, 'elevator'),
                    (gen_random_uuid(), 'BeachAccess', 'دخول الشاطئ', 'Beach access', 32, 'beach access'),
                    (gen_random_uuid(), 'Clubhouse', 'نادي اجتماعي', 'Clubhouse', 33, 'clubhouse'),
                    (gen_random_uuid(), 'SmartHome', 'منزل ذكي', 'Smart home', 34, 'smart home'),
                    (gen_random_uuid(), 'WalkingTrack', 'ممشى', 'Walking track', 35, 'walking track'),
                    (gen_random_uuid(), 'NaturalGas', 'غاز طبيعي', 'Natural gas', 36, 'natural gas'),
                    (gen_random_uuid(), 'MaintenanceService', 'خدمة صيانة', 'Maintenance service', 37, 'maintenance service'),
                    (gen_random_uuid(), 'ElectricityMeter', 'عداد كهرباء', 'Electricity meter', 38, 'electricity meter'),
                    (gen_random_uuid(), 'WaterMeter', 'عداد مياه', 'Water meter', 39, 'water meter'),
                    (gen_random_uuid(), 'InternetReady', 'جاهز للإنترنت', 'Internet ready', 40, 'internet ready'),
                    (gen_random_uuid(), 'Kitchen', 'مطبخ', 'Kitchen', 41, 'kitchen')
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property_views",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "views",
                schema: "registry");

            // The amenities added by Up go, with any link to them; the others only lose their AI code.
            migrationBuilder.Sql(
                """
                DELETE FROM registry.property_amenities
                WHERE amenity_id IN (SELECT id FROM registry.amenities WHERE code IN ('Garden', 'Elevator', 'BeachAccess', 'Clubhouse', 'SmartHome', 'WalkingTrack', 'NaturalGas', 'MaintenanceService', 'ElectricityMeter', 'WaterMeter', 'InternetReady', 'Kitchen'));

                DELETE FROM registry.amenities WHERE code IN ('Garden', 'Elevator', 'BeachAccess', 'Clubhouse', 'SmartHome', 'WalkingTrack', 'NaturalGas', 'MaintenanceService', 'ElectricityMeter', 'WaterMeter', 'InternetReady', 'Kitchen');
                """);

            migrationBuilder.DropColumn(
                name: "ai_code",
                schema: "registry",
                table: "amenities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_ai_place",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_ai_sync_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_area_sqm_range",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_floor_range",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_listing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_price_positive",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_transaction_type",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_type",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties");

            // Back to the old vocabulary. Lossy by nature: every residential type becomes Unit and every other
            // non-land type becomes Building; "furnished" returns as a finishing status.
            migrationBuilder.Sql(
                """
                UPDATE registry.properties SET finishing_status = CASE
                    WHEN furnished IS TRUE AND finishing_status IN ('Finished', 'LuxuryFinished') THEN 'Furnished'
                    WHEN finishing_status = 'Finished' THEN 'FullyFinished'
                    WHEN finishing_status = 'LuxuryFinished' THEN 'SuperLux'
                    WHEN finishing_status = 'CoreAndShell' THEN 'Unfinished'
                    ELSE finishing_status
                END
                WHERE finishing_status IS NOT NULL;

                UPDATE registry.properties SET type = CASE
                    WHEN type IN ('Land', 'Building') THEN type
                    WHEN type IN ('Office', 'Shop', 'Warehouse', 'Farm') THEN 'Building'
                    ELSE 'Unit'
                END;
                """);

            migrationBuilder.DropColumn(
                name: "ai_area_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_compound_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_sync_error",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_sync_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_synced_at",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "area_sqm",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "description_ar",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "description_en",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "floor",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "furnished",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "listing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "price",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "title_ar",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "title_en",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "transaction_type",
                schema: "registry",
                table: "properties");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_type",
                schema: "registry",
                table: "properties",
                sql: "type IN ('Land', 'Building', 'Unit')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties",
                sql: "finishing_status IS NULL OR finishing_status IN ('Unfinished', 'SemiFinished', 'FullyFinished', 'SuperLux', 'Furnished')");

            // Fails if a marketplace listing already uses a property type above 3; those listings must be dealt
            // with first, because the old constraint cannot hold them.
            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_submissions_content",
                schema: "marketplace",
                table: "submissions",
                sql: "COALESCE((content_snapshot->>'FormatVersion' = '1' AND jsonb_typeof(content_snapshot->'Payload'->'Country') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Country')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Region') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Region')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'City') = 'string' AND length(btrim(content_snapshot->'Payload'->>'City')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'District') = 'string' AND length(btrim(content_snapshot->'Payload'->>'District')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Title') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Title')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Description') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Description')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Currency') = 'string' AND length(btrim(content_snapshot->'Payload'->>'Currency')) > 0 AND jsonb_typeof(content_snapshot->'Payload'->'Amount') = 'number' AND content_snapshot->'Payload'->>'PropertyType' IN ('1','2','3') AND ((purpose = 'Sale' AND content_snapshot->'Payload'->>'Purpose' = '1' AND content_snapshot->'Payload'->'RentPeriod' = 'null'::jsonb) OR (purpose = 'Rent' AND content_snapshot->'Payload'->>'Purpose' = '2' AND content_snapshot->'Payload'->>'RentPeriod' IN ('1','2')))), false)");
        }
    }
}
