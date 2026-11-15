using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceRegistrationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_registration_requests",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    applicant_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    commercial_registration_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_registration_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_registration_requests_users_applicant_user_id",
                        column: x => x.applicant_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_registration_requests_users_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_registration_requests_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalSchema: "platform",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_workspace_registration_requests_status_requested_at",
                schema: "identity",
                table: "workspace_registration_requests",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_registration_requests_reviewed_by_user_id",
                schema: "identity",
                table: "workspace_registration_requests",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_registration_requests_workspace_id",
                schema: "identity",
                table: "workspace_registration_requests",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_identity_workspace_registration_requests_pending_applicant",
                schema: "identity",
                table: "workspace_registration_requests",
                column: "applicant_user_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_registration_requests",
                schema: "identity");
        }
    }
}
