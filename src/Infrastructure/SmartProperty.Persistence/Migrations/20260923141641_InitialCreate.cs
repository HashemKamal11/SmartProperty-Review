using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.CheckConstraint("ck_identity_refresh_tokens_expires_after_created", "expires_at > created_at");
                    table.CheckConstraint("ck_identity_refresh_tokens_revoked_after_created", "revoked_at IS NULL OR revoked_at >= created_at");
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_credentials",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_credentials", x => x.user_id);
                    table.CheckConstraint("ck_identity_user_credentials_updated_after_created", "updated_at >= created_at");
                    table.ForeignKey(
                        name: "FK_user_credentials_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                    table.CheckConstraint("ck_identity_roles_scope_workspace", "(scope = 'Platform' AND workspace_id IS NULL) OR (scope = 'Workspace' AND workspace_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_roles_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalSchema: "platform",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_access_requests",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_access_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_access_requests_users_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_access_requests_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_access_requests_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalSchema: "platform",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_memberships",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_memberships_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalSchema: "platform",
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "identity",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalSchema: "identity",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_platform_roles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_platform_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_user_platform_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_platform_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_membership_roles",
                schema: "identity",
                columns: table => new
                {
                    workspace_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_membership_roles", x => new { x.workspace_membership_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_workspace_membership_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workspace_membership_roles_workspace_memberships_workspace_~",
                        column: x => x.workspace_membership_id,
                        principalSchema: "identity",
                        principalTable: "workspace_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_identity_permissions_code",
                schema: "identity",
                table: "permissions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_refresh_tokens_user_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_identity_refresh_tokens_token_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_id",
                schema: "identity",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_roles_workspace_id",
                schema: "identity",
                table: "roles",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_platform_roles_role_id",
                schema: "identity",
                table: "user_platform_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_identity_users_email",
                schema: "identity",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_workspace_access_requests_user_workspace_status",
                schema: "identity",
                table: "workspace_access_requests",
                columns: new[] { "user_id", "workspace_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_access_requests_reviewed_by_user_id",
                schema: "identity",
                table: "workspace_access_requests",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_access_requests_workspace_id",
                schema: "identity",
                table: "workspace_access_requests",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_membership_roles_role_id",
                schema: "identity",
                table: "workspace_membership_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memberships_workspace_id",
                schema: "identity",
                table: "workspace_memberships",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_identity_workspace_memberships_user_workspace",
                schema: "identity",
                table: "workspace_memberships",
                columns: new[] { "user_id", "workspace_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_credentials",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_platform_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "workspace_access_requests",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "workspace_membership_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "workspace_memberships",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "workspaces",
                schema: "platform");
        }
    }
}
