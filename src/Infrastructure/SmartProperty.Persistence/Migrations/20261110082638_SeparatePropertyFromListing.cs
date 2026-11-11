using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparatePropertyFromListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Commercial and advertisement data leaves the physical property. It is never discarded: every value is
            // copied to registry.legacy_property_commercial first, and only then are the columns dropped. The table is
            // a read-only archive (no model maps it) and is the source for pre-filling a new draft listing. No listing
            // is created and nothing is published here: a listing submission needs verified legal authority, which
            // legacy property rows do not carry.
            migrationBuilder.Sql("""
                CREATE TABLE registry.legacy_property_commercial (
                    property_id uuid NOT NULL,
                    transaction_type character varying(32) NULL,
                    price numeric(18,2) NULL,
                    listing_status character varying(32) NOT NULL,
                    title_ar character varying(300) NULL,
                    title_en character varying(300) NULL,
                    description_ar character varying(10000) NULL,
                    description_en character varying(10000) NULL,
                    ai_sync_status character varying(32) NOT NULL,
                    ai_synced_at timestamp with time zone NULL,
                    ai_sync_error character varying(1000) NULL,
                    migrated_at timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT pk_legacy_property_commercial PRIMARY KEY (property_id),
                    CONSTRAINT fk_legacy_property_commercial_property FOREIGN KEY (property_id)
                        REFERENCES registry.properties (id) ON DELETE RESTRICT
                );

                INSERT INTO registry.legacy_property_commercial (
                    property_id, transaction_type, price, listing_status, title_ar, title_en,
                    description_ar, description_en, ai_sync_status, ai_synced_at, ai_sync_error)
                SELECT id, transaction_type, price, listing_status, title_ar, title_en,
                       description_ar, description_en, ai_sync_status, ai_synced_at, ai_sync_error
                FROM registry.properties
                WHERE transaction_type IS NOT NULL OR price IS NOT NULL
                   OR title_ar IS NOT NULL OR title_en IS NOT NULL
                   OR description_ar IS NOT NULL OR description_en IS NOT NULL
                   OR listing_status <> 'Available'
                   OR ai_sync_status <> 'NotSynced' OR ai_synced_at IS NOT NULL OR ai_sync_error IS NOT NULL;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_ai_sync_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_listing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_price_positive",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropCheckConstraint(
                name: "ck_registry_properties_transaction_type",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_sync_error",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_sync_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "ai_synced_at",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "description_ar",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "description_en",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "listing_status",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "price",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "title_ar",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "title_en",
                schema: "registry",
                table: "properties");

            migrationBuilder.DropColumn(
                name: "transaction_type",
                schema: "registry",
                table: "properties");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_sync_error",
                schema: "registry",
                table: "properties",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_sync_status",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotSynced");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_synced_at",
                schema: "registry",
                table: "properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description_ar",
                schema: "registry",
                table: "properties",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description_en",
                schema: "registry",
                table: "properties",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "listing_status",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<decimal>(
                name: "price",
                schema: "registry",
                table: "properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title_ar",
                schema: "registry",
                table: "properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title_en",
                schema: "registry",
                table: "properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transaction_type",
                schema: "registry",
                table: "properties",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_ai_sync_status",
                schema: "registry",
                table: "properties",
                sql: "ai_sync_status IN ('NotSynced', 'Synced', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_listing_status",
                schema: "registry",
                table: "properties",
                sql: "listing_status IN ('Available', 'Reserved', 'Sold', 'Rented')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_price_positive",
                schema: "registry",
                table: "properties",
                sql: "price IS NULL OR price > 0");

            // Put the archived values back before the archive goes. Properties created after the Up migration have
            // no archived row and keep the column defaults.
            migrationBuilder.Sql("""
                UPDATE registry.properties AS p
                SET transaction_type = l.transaction_type,
                    price = l.price,
                    listing_status = l.listing_status,
                    title_ar = l.title_ar,
                    title_en = l.title_en,
                    description_ar = l.description_ar,
                    description_en = l.description_en,
                    ai_sync_status = l.ai_sync_status,
                    ai_synced_at = l.ai_synced_at,
                    ai_sync_error = l.ai_sync_error
                FROM registry.legacy_property_commercial AS l
                WHERE l.property_id = p.id;

                DROP TABLE registry.legacy_property_commercial;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_registry_properties_transaction_type",
                schema: "registry",
                table: "properties",
                sql: "transaction_type IS NULL OR transaction_type IN ('Sale', 'Rent')");
        }
    }
}
