using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone_number",
                schema: "identity",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_verified_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "phone_verifications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phone_verifications", x => x.id);
                    table.CheckConstraint("ck_identity_phone_verifications_expires_after_created", "expires_at > created_at");
                    table.CheckConstraint("ck_identity_phone_verifications_failed_attempts_not_negative", "failed_attempts >= 0");
                    table.ForeignKey(
                        name: "FK_phone_verifications_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_identity_users_phone_number",
                schema: "identity",
                table: "users",
                column: "phone_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_identity_phone_verifications_user_id_created_at",
                schema: "identity",
                table: "phone_verifications",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "phone_verifications",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "ux_identity_users_phone_number",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_number",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone_verified_at",
                schema: "identity",
                table: "users");
        }
    }
}
