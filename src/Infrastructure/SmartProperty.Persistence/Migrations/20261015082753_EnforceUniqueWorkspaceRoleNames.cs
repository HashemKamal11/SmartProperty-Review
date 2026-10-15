using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueWorkspaceRoleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_identity_roles_workspace_name",
                schema: "identity",
                table: "roles",
                columns: new[] { "workspace_id", "name" },
                unique: true,
                filter: "scope = 'Workspace'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_identity_roles_workspace_name",
                schema: "identity",
                table: "roles");
        }
    }
}
