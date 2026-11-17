using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartProperty.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminCompletionReturnsToAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests");
 
            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL) AND (legal_reviewed_by_user_id IS NULL) = (reviewed_property_updated_at IS NULL) AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL) AND (status <> 'PendingLegalReview' OR legal_reviewed_by_user_id IS NULL) AND (status <> 'Draft' OR legal_reviewed_by_user_id IS NULL OR completion_requested_at_stage = 'PendingIssuance')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_authority_requests_legal_review",
                schema: "marketplace",
                table: "authority_requests",
                sql: "(legal_reviewed_by_user_id IS NULL) = (legal_reviewed_at IS NULL) AND (legal_reviewed_by_user_id IS NULL) = (reviewed_property_updated_at IS NULL) AND (status NOT IN ('PendingIssuance', 'Approved') OR legal_reviewed_by_user_id IS NOT NULL) AND (status NOT IN ('Draft', 'PendingLegalReview') OR legal_reviewed_by_user_id IS NULL)");
        }
    }
}
