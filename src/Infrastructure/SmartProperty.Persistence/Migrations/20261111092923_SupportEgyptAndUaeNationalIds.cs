using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportEgyptAndUaeNationalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "national_id",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "id_country_code",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            // Every ID stored so far passed the old rule (10 digits, the Saudi format), so it is a Saudi ID.
            migrationBuilder.Sql(
                "UPDATE identity.owner_profiles SET id_country_code = 'SA' WHERE national_id IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "ux_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles",
                columns: new[] { "id_country_code", "national_id" },
                unique: true,
                filter: "national_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles",
                sql: "(national_id IS NULL AND id_country_code IS NULL) OR (national_id IS NOT NULL AND id_country_code IS NOT NULL AND ((id_country_code = 'SA' AND national_id ~ '^[0-9]{10}$') OR (id_country_code = 'EG' AND national_id ~ '^[23][0-9]{13}$') OR (id_country_code = 'AE' AND national_id ~ '^784[0-9]{12}$')))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old schema can only hold Saudi IDs. Refuse rather than delete Egyptian or Emirati ones.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM identity.owner_profiles WHERE id_country_code <> 'SA') THEN
                        RAISE EXCEPTION 'Cannot roll back: owner profiles hold non-Saudi national IDs.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.DropCheckConstraint(
                name: "ck_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.DropColumn(
                name: "id_country_code",
                schema: "identity",
                table: "owner_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "national_id",
                schema: "identity",
                table: "owner_profiles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles",
                column: "national_id",
                unique: true,
                filter: "national_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_identity_owner_profiles_national_id",
                schema: "identity",
                table: "owner_profiles",
                sql: "national_id IS NULL OR national_id ~ '^[0-9]{10}$'");
        }
    }
}
