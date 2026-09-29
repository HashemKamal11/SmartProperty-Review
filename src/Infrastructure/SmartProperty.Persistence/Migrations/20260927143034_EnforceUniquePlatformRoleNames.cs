using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniquePlatformRoleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_identity_roles_platform_name",
                schema: "identity",
                table: "roles",
                column: "name",
                unique: true,
                filter: "scope = 'Platform'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_identity_roles_platform_name",
                schema: "identity",
                table: "roles");
        }
    }
}
