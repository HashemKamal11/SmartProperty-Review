using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <summary>
    /// Lets a user keep a list of favorite properties: one row per user and property.
    /// </summary>
    /// <remarks>
    /// The primary key is the pair, so marking a property twice cannot make two rows. Neither foreign key cascades:
    /// users are deactivated and properties archived, never deleted, so a delete that would orphan a favorite is refused.
    /// </remarks>
    public partial class AddFavoriteProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "favorite_properties",
                schema: "registry",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorite_properties", x => new { x.user_id, x.property_id });
                    table.ForeignKey(
                        name: "FK_favorite_properties_properties_property_id",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_favorite_properties_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_favorite_properties_property_id",
                schema: "registry",
                table: "favorite_properties",
                column: "property_id");

            migrationBuilder.CreateIndex(
                name: "IX_favorite_properties_user_id_created_at",
                schema: "registry",
                table: "favorite_properties",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorite_properties",
                schema: "registry");
        }
    }
}
