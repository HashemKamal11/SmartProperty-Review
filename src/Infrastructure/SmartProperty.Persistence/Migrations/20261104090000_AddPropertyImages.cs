using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <summary>
    /// Gives a property an ordered group of photos. The files live in the image storage; this table holds only where
    /// each one is, what it is, and its place in the group (the first is the cover).
    /// </summary>
    public partial class AddPropertyImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "property_images",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_images", x => x.id);
                    table.CheckConstraint("ck_registry_property_images_content_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_registry_property_images_size", "size_bytes > 0 AND size_bytes <= 5242880");
                    table.CheckConstraint("ck_registry_property_images_sort_order", "sort_order >= 0");
                    table.ForeignKey(
                        name: "FK_property_images_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_property_images_property_id_sort_order",
                schema: "registry",
                table: "property_images",
                columns: new[] { "property_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_registry_property_images_storage_key",
                schema: "registry",
                table: "property_images",
                column: "storage_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property_images",
                schema: "registry");
        }
    }
}
