using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Proves the current EF model, the real <c>ApplicationDbContext</c>, and Npgsql can build the schema on
/// PostgreSQL, and that the objects the rest of the suite relies on are the ones the configurations declare.
/// </summary>
/// <remarks>
/// This is a model test, not a deployment test. The schema comes from <c>EnsureCreated</c> against the current
/// model; the repository has no migrations, and this step adds none. Nothing here says anything about migration
/// ordering, upgrade paths, rollback, or the operational safety of a production deployment.
/// </remarks>
public sealed class SchemaTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task TheModelCreatesEveryConfiguredTable()
    {
        string[] expected =
        [
            "identity.one_time_codes",
            "identity.owner_profiles",
            "identity.permissions",
            "identity.refresh_tokens",
            "identity.role_permissions",
            "identity.roles",
            "identity.user_credentials",
            "identity.user_platform_roles",
            "identity.users",
            "identity.workspace_access_requests",
            "identity.workspace_membership_roles",
            "identity.workspace_memberships",
            "identity.workspace_registration_requests",
            "platform.workspaces"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT table_schema || '.' || table_name
            FROM information_schema.tables
            WHERE table_schema IN ('identity', 'platform') AND table_type = 'BASE TABLE'
            ORDER BY 1;
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TheModelCreatesBothSchemas()
    {
        string[] expected = ["identity", "platform"];

        var actual = await QueryStringsAsync(
            """
            SELECT schema_name
            FROM information_schema.schemata
            WHERE schema_name IN ('identity', 'platform')
            ORDER BY 1;
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TheModelCreatesTheNamedUniqueIndexes()
    {
        string[] expected =
        [
            "ux_identity_owner_profiles_national_id",
            "ux_identity_owner_profiles_user_id",
            "ux_identity_permissions_code",
            "ux_identity_refresh_tokens_token_hash",
            "ux_identity_roles_platform_name",
            "ux_identity_roles_workspace_name",
            "ux_identity_users_email",
            "ux_identity_users_phone_number",
            "ux_identity_workspace_memberships_user_workspace",
            "ux_identity_workspace_registration_requests_pending_applicant"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'identity' AND indexname LIKE 'ux_%'
            ORDER BY 1;
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TheModelCreatesTheNamedCheckConstraints()
    {
        string[] expected =
        [
            "ck_identity_one_time_codes_expires_after_created",
            "ck_identity_one_time_codes_failed_attempts_not_negative",
            "ck_identity_owner_profiles_address_numbers",
            "ck_identity_owner_profiles_national_id",
            "ck_identity_owner_profiles_review",
            "ck_identity_owner_profiles_status",
            "ck_identity_refresh_tokens_expires_after_created",
            "ck_identity_refresh_tokens_revoked_after_created",
            "ck_identity_roles_scope_workspace",
            "ck_identity_user_credentials_updated_after_created",
            "ck_locations_countries_code",
            "ck_marketplace_authority_grants_ids",
            "ck_marketplace_authority_grants_independence",
            "ck_marketplace_authority_grants_revocation",
            "ck_marketplace_authority_grants_snapshots",
            "ck_marketplace_authority_grants_values",
            "ck_marketplace_authority_request_decisions_kind",
            "ck_marketplace_authority_request_decisions_staff",
            "ck_marketplace_authority_request_documents_content",
            "ck_marketplace_authority_request_documents_scan",
            "ck_marketplace_authority_request_documents_type",
            "ck_marketplace_authority_request_documents_version",
            "ck_marketplace_authority_requests_capacity",
            "ck_marketplace_authority_requests_grants",
            "ck_marketplace_authority_requests_independence",
            "ck_marketplace_authority_requests_legal_review",
            "ck_marketplace_authority_requests_listings",
            "ck_marketplace_authority_requests_outcomes",
            "ck_marketplace_authority_requests_purposes",
            "ck_marketplace_authority_requests_stages",
            "ck_marketplace_authority_requests_status",
            "ck_marketplace_commitments_history",
            "ck_marketplace_commitments_money",
            "ck_marketplace_commitments_shape",
            "ck_marketplace_commitments_state",
            "ck_marketplace_decisions_identity",
            "ck_marketplace_decisions_reason",
            "ck_marketplace_decisions_snapshots",
            "ck_marketplace_discard_audits_identity",
            "ck_marketplace_discard_audits_values",
            "ck_marketplace_listings_commitment_closure",
            "ck_marketplace_listings_history",
            "ck_marketplace_listings_ids",
            "ck_marketplace_listings_reservation_closure",
            "ck_marketplace_listings_state",
            "ck_marketplace_property_reservations_ids",
            "ck_marketplace_property_reservations_purpose",
            "ck_marketplace_property_reservations_winner",
            "ck_marketplace_purchase_requests_history",
            "ck_marketplace_purchase_requests_ids",
            "ck_marketplace_purchase_requests_state",
            "ck_marketplace_purchase_requests_terms",
            "ck_marketplace_rental_requests_history",
            "ck_marketplace_rental_requests_ids",
            "ck_marketplace_rental_requests_state",
            "ck_marketplace_rental_requests_terms",
            "ck_marketplace_settings_signing_window",
            "ck_marketplace_settings_singleton",
            "ck_marketplace_submissions_content",
            "ck_marketplace_submissions_evidence",
            "ck_marketplace_submissions_identity",
            "ck_marketplace_submissions_rent_terms",
            "ck_marketplace_submissions_revision",
            "ck_marketplace_submissions_state",
            "ck_matching_property_matches_match_percentage",
            "ck_registry_properties_address_country_code",
            "ck_registry_properties_area_sqm_range",
            "ck_registry_properties_bathrooms_range",
            "ck_registry_properties_bedrooms_range",
            "ck_registry_properties_finishing_status",
            "ck_registry_properties_floor_range",
            "ck_registry_properties_latitude_range",
            "ck_registry_properties_longitude_range",
            "ck_registry_properties_place",
            "ck_registry_properties_status",
            "ck_registry_properties_type",
            "ck_registry_property_comparison_items_position",
            "ck_registry_property_images_content_type",
            "ck_registry_property_images_size",
            "ck_registry_property_images_sort_order"
        ];

        var actual = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE contype = 'c' AND conname LIKE 'ck_%'
            ORDER BY 1;
            """);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task NoForeignKeyIsCreatedWithACascadingDeleteOrUpdate()
    {
        // Every relationship is configured DeleteBehavior.Restrict, which PostgreSQL records as 'r'; 'a' is the
        // NO ACTION default. Anything else — 'c' cascade, 'n' set null, 'd' set default — would mean a delete
        // could silently remove identity or access rows, which is exactly what must not happen.
        var permissive = await QueryStringsAsync(
            """
            SELECT conname
            FROM pg_constraint
            WHERE contype = 'f' AND (confdeltype NOT IN ('a', 'r') OR confupdtype NOT IN ('a', 'r'))
            ORDER BY 1;
            """);

        Assert.Empty(permissive);
    }

    [Fact]
    public async Task TheContextCanBeCreatedAndQueriedThroughNpgsql()
    {
        await using var context = Host.CreateVerificationContext();

        Assert.True(await context.Database.CanConnectAsync());
        Assert.Empty(await context.Users.ToListAsync());
    }

    [Fact]
    public void WorkspaceAccessRequestStatusIsAnOptimisticConcurrencyToken()
    {
        using var context = Host.CreateVerificationContext();
        var entityType = context.Model.FindEntityType(typeof(WorkspaceAccessRequest));
        var status = entityType!.FindProperty(nameof(WorkspaceAccessRequest.Status));

        Assert.True(status!.IsConcurrencyToken);
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
