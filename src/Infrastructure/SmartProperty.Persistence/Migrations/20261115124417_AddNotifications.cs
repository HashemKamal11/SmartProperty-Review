using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visibility_kind = table.Column<short>(type: "smallint", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_parameters", "parameters = '{}'::jsonb");
                    table.CheckConstraint("ck_notifications_read_after_created", "read_at IS NULL OR read_at >= created_at");
                    table.CheckConstraint("ck_notifications_subject", "COALESCE((type = 'workspace.member_added' AND subject_type IS NOT DISTINCT FROM 'workspace' AND subject_id IS NOT NULL) OR (type = 'security.password_changed' AND subject_type IS NULL AND subject_id IS NULL), false)");
                    table.CheckConstraint("ck_notifications_type", "type IN ('workspace.member_added', 'security.password_changed')");
                    table.CheckConstraint("ck_notifications_visibility_kind", "visibility_kind = 1");
                    table.ForeignKey(
                        name: "FK_notifications_users_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_created",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_unread",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_recipient_type_occurrence",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "recipient_user_id", "type", "occurrence_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Strict Down: always refuse, even when the table is empty. The raise is the only statement, so the
            // migration transaction rolls back and the schema, its rows and the migration history are kept.
            migrationBuilder.Sql("""
                DO $notifications_down$
                BEGIN
                    RAISE EXCEPTION 'AddNotifications cannot be rolled back automatically.'
                        USING ERRCODE = '55000', HINT = 'notifications.down_refused';
                END;
                $notifications_down$;
                """);
        }
    }
}
