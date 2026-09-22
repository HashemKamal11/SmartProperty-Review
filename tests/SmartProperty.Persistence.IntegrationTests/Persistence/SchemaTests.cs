using Microsoft.EntityFrameworkCore;
using Npgsql;
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
            "ux_identity_permissions_code",
            "ux_identity_refresh_tokens_token_hash",
            "ux_identity_users_email",
            "ux_identity_workspace_memberships_user_workspace"
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
            "ck_identity_refresh_tokens_expires_after_created",
            "ck_identity_refresh_tokens_revoked_after_created",
            "ck_identity_roles_scope_workspace",
            "ck_identity_user_credentials_updated_after_created"
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
