using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <summary>
    /// Gives this platform its own place tree (country, city, area, compound) and links a property to it, replacing
    /// the two bare AI service ids the previous migration put on the property.
    /// </summary>
    /// <remarks>
    /// The place tables start empty: they are filled from the AI search service the first time a level is asked
    /// for. A property can therefore not be linked to a place during this migration, so the old ai_area_id and
    /// ai_compound_id columns are dropped without a copy. No property could have held a meaningful value in them: the
    /// only way to obtain those ids was the AI service's own list, which the platform did not yet store.
    /// </remarks>
    public partial class AddLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "locations");

            migrationBuilder.CreateTable(
                name: "countries",
                schema: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_id = table.Column<int>(type: "integer", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cities",
                schema: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_id = table.Column<int>(type: "integer", nullable: false),
                    country_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cities", x => x.id);
                    table.ForeignKey(
                        name: "FK_cities_countries_country_id",
                        column: x => x.country_id,
                        principalSchema: "locations",
                        principalTable: "countries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "areas",
                schema: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_id = table.Column<int>(type: "integer", nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_areas", x => x.id);
                    table.ForeignKey(
                        name: "FK_areas_cities_city_id",
                        column: x => x.city_id,
                        principalSchema: "locations",
                        principalTable: "cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "compounds",
                schema: "locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_id = table.Column<int>(type: "integer", nullable: false),
                    area_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name_ar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compounds", x => x.id);
                    table.ForeignKey(
                        name: "FK_compounds_areas_area_id",
                        column: x => x.area_id,
                        principalSchema: "locations",
                        principalTable: "areas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_locations_countries_ai_id",
                schema: "locations",
                table: "countries",
                column: "ai_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_locations_cities_ai_id",
                schema: "locations",
                table: "cities",
                column: "ai_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cities_country_id",
                schema: "locations",
                table: "cities",
                column: "country_id");

            migrationBuilder.CreateIndex(
                name: "ux_locations_areas_ai_id",
                schema: "locations",
                table: "areas",
                column: "ai_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_areas_city_id",
                schema: "locations",
                table: "areas",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "ux_locations_compounds_ai_id",
                schema: "locations",
                table: "compounds",
                column: "ai_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_compounds_area_id",
                schema: "locations",
                table: "compounds",
                column: "area_id");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_ai_place",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_area_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_compound_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.AddColumn<Guid>(
                name: "area_id",
                schema: "registry",
                table: "properties",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "compound_id",
                schema: "registry",
                table: "properties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_properties_area_id",
                schema: "registry",
                table: "properties",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "IX_properties_compound_id",
                schema: "registry",
                table: "properties",
                column: "compound_id");

            migrationBuilder.AddForeignKey(
                name: "FK_properties_areas_area_id",
                schema: "registry",
                table: "properties",
                column: "area_id",
                principalSchema: "locations",
                principalTable: "areas",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_properties_compounds_compound_id",
                schema: "registry",
                table: "properties",
                column: "compound_id",
                principalSchema: "locations",
                principalTable: "compounds",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_place",
                schema: "registry",
                table: "properties",
                sql: "compound_id IS NULL OR area_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_place",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropForeignKey(
                name: "FK_properties_compounds_compound_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropForeignKey(
                name: "FK_properties_areas_area_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropIndex(
                name: "IX_properties_compound_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropIndex(
                name: "IX_properties_area_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "compound_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "area_id",
                schema: "registry",
                table: "properties");

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

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_ai_place",
                schema: "registry",
                table: "properties",
                sql: "(ai_area_id IS NULL OR ai_area_id > 0) AND (ai_compound_id IS NULL OR (ai_compound_id > 0 AND ai_area_id IS NOT NULL))");

            migrationBuilder.DropTable(
                name: "compounds",
                schema: "locations");

            migrationBuilder.DropTable(
                name: "areas",
                schema: "locations");

            migrationBuilder.DropTable(
                name: "cities",
                schema: "locations");

            migrationBuilder.DropTable(
                name: "countries",
                schema: "locations");
        }
    }
}
