using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserContactVerificationAndOneTimeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // phone_verifications only ever held codes that expire after five minutes, and the code hash now also
            // covers the purpose, so no old code could match anyway. one_time_codes replaces it for every purpose.
            migrationBuilder.DropTable(
                name: "phone_verifications",
                schema: "identity");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "one_time_codes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_one_time_codes", x => x.id);
                    table.CheckConstraint("ck_identity_one_time_codes_expires_after_created", "expires_at > created_at");
                    table.CheckConstraint("ck_identity_one_time_codes_failed_attempts_not_negative", "failed_attempts >= 0");
                    table.ForeignKey(
                        name: "FK_one_time_codes_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_one_time_codes_user_id_purpose_created_at",
                schema: "identity",
                table: "one_time_codes",
                columns: new[] { "user_id", "purpose", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "one_time_codes",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                schema: "identity",
                table: "users");

            migrationBuilder.CreateTable(
                name: "phone_verifications",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "ix_identity_phone_verifications_user_id_created_at",
                schema: "identity",
                table: "phone_verifications",
                columns: new[] { "user_id", "created_at" });
        }
    }
}
