using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorityGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "authority_grants",
                schema: "marketplace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    authority_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    grantee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    issued_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_review_reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_reviewer_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    legal_reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_authority_grants", x => x.id);
                    table.CheckConstraint("ck_marketplace_authority_grants_ids", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND property_id <> '00000000-0000-0000-0000-000000000000'::uuid AND grantee_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND legal_review_reference_id <> '00000000-0000-0000-0000-000000000000'::uuid AND issued_by_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND legal_reviewed_by_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_marketplace_authority_grants_independence", "issued_by_user_id <> grantee_user_id AND legal_reviewed_by_user_id <> grantee_user_id AND legal_reviewed_by_user_id <> issued_by_user_id");
                    table.CheckConstraint("ck_marketplace_authority_grants_revocation", "(revoked_at IS NULL AND revoked_by_user_id IS NULL AND version = 1) OR (revoked_at IS NOT NULL AND revoked_by_user_id IS NOT NULL AND revoked_at >= issued_at AND version > 1)");
                    table.CheckConstraint("ck_marketplace_authority_grants_snapshots", "COALESCE((authority_snapshot->>'FormatVersion' = '1' AND issuer_snapshot->>'FormatVersion' = '1' AND legal_reviewer_snapshot->>'FormatVersion' = '1' AND authority_snapshot->'Payload'->>'PropertyId' = property_id::text AND authority_snapshot->'Payload'->>'ActingUserId' = grantee_user_id::text AND authority_snapshot->'Payload'->>'Purpose' = (CASE purpose WHEN 'Sale' THEN '1' ELSE '2' END) AND authority_snapshot->'Payload'->>'Representation' IN ('1','2') AND authority_snapshot->'Payload'->'WorkspaceId' = 'null'::jsonb AND jsonb_typeof(authority_snapshot->'Payload'->'ValidUntil') = 'string' AND issuer_snapshot->'Payload'->>'ReviewerUserId' = issued_by_user_id::text AND legal_reviewer_snapshot->'Payload'->>'ReviewerUserId' = legal_reviewed_by_user_id::text), false)");
                    table.CheckConstraint("ck_marketplace_authority_grants_values", "purpose IN ('Sale','Rent') AND version > 0 AND legal_reviewed_at <= issued_at AND isfinite(issued_at) AND isfinite(legal_reviewed_at)");
                    table.ForeignKey(
                        name: "fk_marketplace_authority_grants_grantee",
                        column: x => x.grantee_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_authority_grants_issuer",
                        column: x => x.issued_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_authority_grants_legal_reviewer",
                        column: x => x.legal_reviewed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_authority_grants_property",
                        column: x => x.property_id,
                        principalSchema: "registry",
                        principalTable: "properties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_authority_grants_revoker",
                        column: x => x.revoked_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authority_grants_issued_by_user_id",
                schema: "marketplace",
                table: "authority_grants",
                column: "issued_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_grants_legal_reviewed_by_user_id",
                schema: "marketplace",
                table: "authority_grants",
                column: "legal_reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_authority_grants_revoked_by_user_id",
                schema: "marketplace",
                table: "authority_grants",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authority_grants_lookup",
                schema: "marketplace",
                table: "authority_grants",
                columns: new[] { "property_id", "grantee_user_id", "purpose", "issued_at" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_authority_grants_active",
                schema: "marketplace",
                table: "authority_grants",
                columns: new[] { "grantee_user_id", "property_id", "purpose" },
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authority_grants",
                schema: "marketplace");
        }
    }
}
