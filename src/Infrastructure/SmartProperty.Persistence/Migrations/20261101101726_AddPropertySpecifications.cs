using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertySpecifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bathrooms",
                schema: "registry",
                table: "properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bedrooms",
                schema: "registry",
                table: "properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "built_date",
                schema: "registry",
                table: "properties",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finishing_status",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_bathrooms_range",
                schema: "registry",
                table: "properties",
                sql: "bathrooms IS NULL OR bathrooms BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_bedrooms_range",
                schema: "registry",
                table: "properties",
                sql: "bedrooms IS NULL OR bedrooms BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties",
                sql: "finishing_status IS NULL OR finishing_status IN ('Unfinished', 'SemiFinished', 'FullyFinished', 'SuperLux', 'Furnished')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_bathrooms_range",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_bedrooms_range",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_finishing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "bathrooms",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "bedrooms",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "built_date",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "finishing_status",
                schema: "registry",
                table: "properties");
        }
    }
}
