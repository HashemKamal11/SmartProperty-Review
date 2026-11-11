using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelaxOwnerAddressNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_owner_profiles_address_numbers",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "building_number",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4)",
                oldMaxLength: 4,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "additional_number",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4)",
                oldMaxLength: 4,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_owner_profiles_address_numbers",
                schema: "identity",
                table: "owner_profiles",
                sql: "(building_number IS NULL OR building_number ~ '^[0-9]{1,10}$') AND (postal_code IS NULL OR postal_code ~ '^[0-9]{5}$') AND (additional_number IS NULL OR additional_number ~ '^[0-9]{1,10}$')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema holds exactly 4 digits. Refuse rather than truncate or delete addresses that do not fit.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM identity.owner_profiles
                        WHERE building_number !~ '^[0-9]{4}$' OR additional_number !~ '^[0-9]{4}$') THEN
                        RAISE EXCEPTION 'Cannot roll back: owner profiles hold building or additional numbers that are not 4 digits.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_owner_profiles_address_numbers",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "building_number",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "additional_number",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_owner_profiles_address_numbers",
                schema: "identity",
                table: "owner_profiles",
                sql: "(building_number IS NULL OR building_number ~ '^[0-9]{4}$') AND (postal_code IS NULL OR postal_code ~ '^[0-9]{5}$') AND (additional_number IS NULL OR additional_number ~ '^[0-9]{4}$')");
        }
    }
}
