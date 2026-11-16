using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ListingAutomaticRelist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "closed_by_commitment_id",
                schema: "marketplace",
                table: "listings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "relist_automatically",
                schema: "marketplace",
                table: "listings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Listings an acceptance already closed: the acceptance closes them at the commitment's own instant, so the
            // link is recovered exactly. Automatic relisting stays off for them until the owner turns it on.
            migrationBuilder.Sql(
                """
                UPDATE marketplace.listings AS l
                SET closed_by_commitment_id = c.id
                FROM marketplace.property_commitments AS c
                WHERE c.property_id = l.property_id
                  AND c.accepted_at = l.withdrawn_at
                  AND l.publication_status = 'Withdrawn'
                  AND l.availability_status = 'Closed'
                  AND l.closed_by_commitment_id IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listings_commitment_closure",
                schema: "marketplace",
                table: "listings",
                sql: "closed_by_commitment_id IS NULL OR (closed_by_commitment_id <> '00000000-0000-0000-0000-000000000000'::uuid AND publication_status = 'Withdrawn' AND availability_status = 'Closed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listings_commitment_closure",
                schema: "marketplace",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "closed_by_commitment_id",
                schema: "marketplace",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "relist_automatically",
                schema: "marketplace",
                table: "listings");
        }
    }
}
