using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyAmenities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "amenities",
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
                    table.PrimaryKey("PK_amenities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "property_amenities",
                schema: "registry",
                columns: table => new
                {
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amenity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_amenities", x => new { x.property_id, x.amenity_id });
                    table.ForeignKey(
                        name: "FK_property_amenities_amenities_amenity_id",
                        column: x => x.amenity_id,
                        principalSchema: "registry",
                        principalTable: "amenities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_property_amenities_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_registry_amenities_code",
                schema: "registry",
                table: "amenities",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_amenities_amenity_id",
                schema: "registry",
                table: "property_amenities",
                column: "amenity_id");

            // The amenity list shown on the property form and the search filters, in display order. Seeded here
            // so every environment starts with the same codes; clients choose by code, never by id.
            migrationBuilder.Sql(
                """
                INSERT INTO registry.amenities (id, code, name_ar, name_en, sort_order) VALUES
                    (gen_random_uuid(), 'Balcony', 'شرفة', 'Balcony', 1),
                    (gen_random_uuid(), 'Security', 'أمن', 'Security', 2),
                    (gen_random_uuid(), 'AirConditioning', 'تكييف', 'Air conditioning', 3),
                    (gen_random_uuid(), 'StudyRoom', 'غرفة دراسة', 'Study room', 4),
                    (gen_random_uuid(), 'Network', 'شبكة', 'Network', 5),
                    (gen_random_uuid(), 'SwimmingPool', 'مسبح', 'Swimming pool', 6),
                    (gen_random_uuid(), 'CentralAirConditioning', 'تكييف مركزي', 'Central air conditioning', 7),
                    (gen_random_uuid(), 'MaidRoom', 'غرفة خادمة', 'Maid room', 8),
                    (gen_random_uuid(), 'PetsAllowed', 'مسموح بالحيوانات الأليفة', 'Pets allowed', 9),
                    (gen_random_uuid(), 'PrivatePool', 'مسبح خاص', 'Private pool', 10),
                    (gen_random_uuid(), 'WaterView', 'إطلالة على الماء', 'Water view', 11),
                    (gen_random_uuid(), 'BuiltInWardrobes', 'خزائن ملابس', 'Built-in wardrobes', 12),
                    (gen_random_uuid(), 'KitchenAppliances', 'أجهزة مطبخ', 'Kitchen appliances', 13),
                    (gen_random_uuid(), 'PrivateGym', 'نادي رياضي خاص', 'Private gym', 14),
                    (gen_random_uuid(), 'SharedGym', 'نادي رياضي عام', 'Shared gym', 15),
                    (gen_random_uuid(), 'PrivateJacuzzi', 'جاكوزي خاص', 'Private jacuzzi', 16),
                    (gen_random_uuid(), 'LandmarkView', 'إطلالة على معلم', 'Landmark view', 17),
                    (gen_random_uuid(), 'WalkInCloset', 'غرفة ملابس', 'Walk-in closet', 18),
                    (gen_random_uuid(), 'ChildrensPool', 'مسبح للأطفال', 'Children''s pool', 19),
                    (gen_random_uuid(), 'BarbecueArea', 'منطقة شواء', 'Barbecue area', 20),
                    (gen_random_uuid(), 'ChildrensPlayArea', 'منطقة لعب للأطفال', 'Children''s play area', 21),
                    (gen_random_uuid(), 'SharedSpa', 'منتجع صحي عام', 'Shared spa', 22),
                    (gen_random_uuid(), 'ConferenceRoom', 'قاعة مؤتمرات', 'Conference room', 23),
                    (gen_random_uuid(), 'Kitchenette', 'مطبخ صغير', 'Kitchenette', 24),
                    (gen_random_uuid(), 'BuildingLobby', 'ردهة في المبنى', 'Building lobby', 25),
                    (gen_random_uuid(), 'ReceptionService', 'خدمة استقبال', 'Reception service', 26),
                    (gen_random_uuid(), 'CoveredParking', 'موقف سيارات مغطى', 'Covered parking', 27),
                    (gen_random_uuid(), 'CleaningService', 'خدمة تنظيف', 'Cleaning service', 28),
                    (gen_random_uuid(), 'BuildingRestaurant', 'مطعم في المبنى', 'Restaurant in the building', 29)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property_amenities",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "amenities",
                schema: "registry");
        }
    }
}
