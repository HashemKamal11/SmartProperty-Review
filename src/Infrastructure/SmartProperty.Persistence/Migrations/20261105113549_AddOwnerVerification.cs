using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "registry",
                table: "properties",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "owner_profiles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    national_id = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    nationality = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    id_expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    identity_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    building_number = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    street_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    district = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    additional_number = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    address_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    address_proof_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    address_proof_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_proof_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_profiles", x => x.id);
                    table.CheckConstraint("ck_identity_owner_profiles_address_numbers", "(building_number IS NULL OR building_number ~ '^[0-9]{4}$') AND (postal_code IS NULL OR postal_code ~ '^[0-9]{5}$') AND (additional_number IS NULL OR additional_number ~ '^[0-9]{4}$')");
                    table.CheckConstraint("ck_identity_owner_profiles_national_id", "national_id IS NULL OR national_id ~ '^[0-9]{10}$'");
                    table.CheckConstraint("ck_identity_owner_profiles_review", "(status IN ('Approved', 'Rejected')) = (reviewed_at IS NOT NULL AND reviewed_by_user_id IS NOT NULL) AND ((status = 'Rejected') = (rejection_reason IS NOT NULL))");
                    table.CheckConstraint("ck_identity_owner_profiles_status", "status IN ('Draft', 'UnderReview', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_owner_profiles_users_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_owner_profiles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_properties_owner_user_id",
                schema: "registry",
                table: "properties",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_owner_profiles_reviewed_by_user_id",
                schema: "identity",
                table: "owner_profiles",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_owner_profiles_status_submitted_at",
                schema: "identity",
                table: "owner_profiles",
                columns: new[] { "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles",
                column: "national_id",
                unique: true,
                filter: "national_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_identity_owner_profiles_user_id",
                schema: "identity",
                table: "owner_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_properties_users_owner_user_id",
                schema: "registry",
                table: "properties",
                column: "owner_user_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_properties_users_owner_user_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropTable(
                name: "owner_profiles",
                schema: "identity");

            migrationBuilder.DropIndex(
                name: "IX_properties_owner_user_id",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "registry",
                table: "properties");
        }
    }
}
