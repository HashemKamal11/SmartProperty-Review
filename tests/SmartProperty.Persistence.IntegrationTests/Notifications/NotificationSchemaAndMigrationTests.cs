using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Notifications;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Notifications;

/// <summary>
/// The notifications schema as the AddNotifications migration builds it (S1–S14): exact objects and rules, catalog
/// parity, fresh bootstrap, upgrade, parity with <c>EnsureCreated</c>, and the Strict Down refusal.
/// </summary>
/// <remarks>
/// Every test first recreates its own private database through EF migrations, so no assertion here is satisfied by
/// the <c>EnsureCreated</c> template. Only this test's own database is ever deleted or migrated; the shared template
/// is only ever copied, and the separate <c>EnsureCreated</c> reference used by S12 is only read.
/// </remarks>
public sealed class NotificationSchemaAndMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PreviousMigration = "20261108122655_AddDailyWeeklyRentPeriods";
    private const string NotificationsMigration = "20261115124417_AddNotifications";

    private const string StrictDownMessage = "AddNotifications cannot be rolled back automatically.";
    private const string StrictDownHint = "notifications.down_refused";

    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow.AddMinutes(1);

    // The frozen PostgreSQL 17 rendering of "type IN (...)" on a varchar column: one positive membership test and
    // nothing else, so no OR, NOT or NULL-accepting branch can fit.
    private static readonly Regex TypeCheckShape = new(
        @"^CHECK \(\(\(type\)::text = ANY \(\(ARRAY\[(?<items>'(?:[^']|'')*'::character varying(?:, '(?:[^']|'')*'::character varying)*)\]\)::text\[\]\)\)\)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TypeLiteral = new(
        @"'(?<value>(?:[^']|'')*)'::character varying",
        RegexOptions.CultureInvariant);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await RebuildFromMigrationsAsync(target: null);
    }

    // S1
    [Fact]
    public async Task S1_TheColumnsHaveTheirExactTypesLengthsAndNullability()
    {
        Assert.Equal(
            [
                "id|uuid||NO",
                "recipient_user_id|uuid||NO",
                "type|character varying|64|NO",
                "occurrence_id|uuid||NO",
                "visibility_kind|smallint||NO",
                "subject_type|character varying|32|YES",
                "subject_id|uuid||YES",
                "parameters|jsonb||NO",
                "created_at|timestamp with time zone||NO",
                "read_at|timestamp with time zone||YES"
            ],
            await ColumnsAsync(Host.ConnectionString));
    }

    // S2
    [Fact]
    public async Task S2_TheConstraintsAreExactlyThePrimaryKeyTheRecipientForeignKeyAndTheFiveChecks()
    {
        Assert.Equal(
            [
                "FK_notifications_users_recipient_user_id|f",
                "PK_notifications|p",
                "ck_notifications_parameters|c",
                "ck_notifications_read_after_created|c",
                "ck_notifications_subject|c",
                "ck_notifications_type|c",
                "ck_notifications_visibility_kind|c"
            ],
            await RowsAsync(
                Host.ConnectionString,
                """
                SELECT conname, contype::text
                FROM pg_constraint
                WHERE conrelid = 'notifications.notifications'::regclass
                ORDER BY conname COLLATE "C"
                """));

        // One local column to identity.users(id); deleting a referenced user is restricted, never cascaded.
        Assert.Equal(
            ["identity.users|r|1|recipient_user_id|id"],
            await RowsAsync(
                Host.ConnectionString,
                """
                SELECT c.confrelid::regclass::text, c.confdeltype::text, array_length(c.conkey, 1)::text,
                       source.attname, target.attname
                FROM pg_constraint AS c
                JOIN pg_attribute AS source ON source.attrelid = c.conrelid AND source.attnum = c.conkey[1]
                JOIN pg_attribute AS target ON target.attrelid = c.confrelid AND target.attnum = c.confkey[1]
                WHERE c.conrelid = 'notifications.notifications'::regclass
                  AND c.conname = 'FK_notifications_users_recipient_user_id'
                """));
    }

    // S3
    [Fact]
    public async Task S3_TheIndexInventoryIncludingThePrimaryKeyIsExact()
    {
        Assert.Equal(
            [
                """PK_notifications|CREATE UNIQUE INDEX "PK_notifications" ON notifications.notifications USING btree (id)""",
                "ix_notifications_recipient_created|CREATE INDEX ix_notifications_recipient_created ON notifications.notifications USING btree (recipient_user_id, created_at DESC, id DESC)",
                "ix_notifications_recipient_unread|CREATE INDEX ix_notifications_recipient_unread ON notifications.notifications USING btree (recipient_user_id, created_at DESC, id DESC) WHERE (read_at IS NULL)",
                "ux_notifications_recipient_type_occurrence|CREATE UNIQUE INDEX ux_notifications_recipient_type_occurrence ON notifications.notifications USING btree (recipient_user_id, type, occurrence_id)"
            ],
            await IndexesAsync(Host.ConnectionString));
    }

    // S4
    [Fact]
    public async Task S4_TheTypeCheckIsOnePositiveMembershipTestOverExactlyTheCatalogTypes()
    {
        var rows = await RowsAsync(
            Host.ConnectionString,
            """
            SELECT c.contype::text,
                   (c.conkey = ARRAY[(SELECT a.attnum FROM pg_attribute AS a
                                      WHERE a.attrelid = c.conrelid AND a.attname = 'type')]::smallint[])::text,
                   pg_get_constraintdef(c.oid)
            FROM pg_constraint AS c
            WHERE c.conrelid = 'notifications.notifications'::regclass AND c.conname = 'ck_notifications_type'
            """);

        // Identity: exactly one CHECK of that name on this table, depending on the type column alone.
        var row = Assert.Single(rows);
        var parts = row.Split('|', 3);
        Assert.Equal("c", parts[0]);
        Assert.Equal("true", parts[1]);

        // Structure: never skipped. An unrecognized rendering fails here with the actual definition in the message.
        var definition = parts[2];
        var shape = TypeCheckShape.Match(definition);
        Assert.True(
            shape.Success,
            $"ck_notifications_type is not the frozen single positive membership test. Actual definition: {definition}");

        var literals = TypeLiteral.Matches(shape.Groups["items"].Value)
            .Select(match => match.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(literals.Count, literals.Distinct(StringComparer.Ordinal).Count());
        Assert.True(
            literals.ToHashSet(StringComparer.Ordinal).SetEquals(NotificationCatalog.ActiveTypes),
            $"Deployed types [{string.Join(", ", literals)}] differ from the catalog [{string.Join(", ", NotificationCatalog.ActiveTypes)}].");
        Assert.Equal(NotificationCatalog.ActiveTypes.Length, literals.Count);

        // Behaviour: each catalog type is accepted; an unsupported one is refused by the intact schema.
        var recipient = await SeedUserAsync();
        Assert.Null(await TryInsertAsync(recipient, NotificationCatalog.MemberAddedType, NotificationCatalog.WorkspaceSubjectType, Guid.NewGuid()));
        Assert.Null(await TryInsertAsync(recipient, NotificationCatalog.PasswordChangedType, null, null));

        var refused = await TryInsertAsync(recipient, "other.kind", null, null);
        AssertCheckViolation(refused, "ck_notifications_type", "ck_notifications_subject");
    }

    // S5
    [Fact]
    public async Task S5_AnUnknownTypeIsRefusedWithoutAssumingWhichCheckFiresFirst()
    {
        var recipient = await SeedUserAsync();

        AssertCheckViolation(
            await TryInsertAsync(recipient, "workspace.member_removed", NotificationCatalog.WorkspaceSubjectType, Guid.NewGuid()),
            "ck_notifications_type",
            "ck_notifications_subject");
        AssertCheckViolation(
            await TryInsertAsync(recipient, "other.kind", null, null),
            "ck_notifications_type",
            "ck_notifications_subject");

        Assert.Equal(0L, await CountNotificationsAsync());
    }

    // S6
    [Fact]
    public async Task S6_TheSubjectRuleIsNullSafeForEveryCombination()
    {
        var recipient = await SeedUserAsync();
        var member = NotificationCatalog.MemberAddedType;
        var password = NotificationCatalog.PasswordChangedType;
        var workspace = NotificationCatalog.WorkspaceSubjectType;

        Assert.Null(await TryInsertAsync(recipient, member, workspace, Guid.NewGuid()));
        Assert.Null(await TryInsertAsync(recipient, password, null, null));

        AssertCheckViolation(await TryInsertAsync(recipient, member, null, Guid.NewGuid()), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, member, workspace, null), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, member, "other", Guid.NewGuid()), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, member, null, null), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, password, workspace, Guid.NewGuid()), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, password, null, Guid.NewGuid()), "ck_notifications_subject");
        AssertCheckViolation(await TryInsertAsync(recipient, password, workspace, null), "ck_notifications_subject");

        Assert.Equal(2L, await CountNotificationsAsync());
    }

    // S7
    [Fact]
    public async Task S7_TheVisibilityParametersAndReadTimeChecksEachRefuseTheirInvalidShape()
    {
        var recipient = await SeedUserAsync();
        var password = NotificationCatalog.PasswordChangedType;

        AssertCheckViolation(
            await TryInsertAsync(recipient, password, null, null, visibilityKind: 2),
            "ck_notifications_visibility_kind");
        AssertCheckViolation(
            await TryInsertAsync(recipient, password, null, null, parameters: """{"a":"b"}"""),
            "ck_notifications_parameters");
        AssertCheckViolation(
            await TryInsertAsync(recipient, password, null, null, readAt: CreatedAt.AddSeconds(-1)),
            "ck_notifications_read_after_created");

        Assert.Null(await TryInsertAsync(recipient, password, null, null, readAt: CreatedAt));
        Assert.Equal(1L, await CountNotificationsAsync());
    }

    // S8
    [Fact]
    public async Task S8_TheOccurrenceIsUniqueAndTheRecipientMustExist()
    {
        var recipient = await SeedUserAsync();
        var occurrence = Guid.NewGuid();
        var password = NotificationCatalog.PasswordChangedType;

        Assert.Null(await TryInsertAsync(recipient, password, null, null, occurrenceId: occurrence));

        var duplicate = await TryInsertAsync(recipient, password, null, null, occurrenceId: occurrence);
        Assert.NotNull(duplicate);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("ux_notifications_recipient_type_occurrence", duplicate.ConstraintName);

        var orphan = await TryInsertAsync(Guid.NewGuid(), password, null, null);
        Assert.NotNull(orphan);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
        Assert.Equal("FK_notifications_users_recipient_user_id", orphan.ConstraintName);

        Assert.Equal(1L, await CountNotificationsAsync());
    }

    // S9
    [Fact]
    public async Task S9_ADuplicateOccurrenceSavedThroughTheUnitOfWorkSurfacesAsAnUnmappedDbUpdateException()
    {
        var recipient = await SeedUserAsync();
        var occurrence = Guid.NewGuid();

        var scope = Host.CreateScope();

        try
        {
            var repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await repository.AddAsync(Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, occurrence, CreatedAt));
            await repository.AddAsync(Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, occurrence, CreatedAt));

            // D1: this index is not a recognized unique constraint, so the exception is not translated.
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("ux_notifications_recipient_type_occurrence", postgres.ConstraintName);
        }
        finally
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                scope.Dispose();
            }
        }

        Assert.Equal(0L, await CountNotificationsAsync());
    }

    // S10
    [Fact]
    public async Task S10_AFreshDatabaseMigratesToAddNotificationsWithNoPendingModelChanges()
    {
        await RebuildFromMigrationsAsync(target: null);

        await using var context = Host.CreateVerificationContext();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Equal(context.Database.GetMigrations(), applied);
        Assert.Equal(NotificationsMigration, applied[^1]);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.True(await TableExistsAsync(Host.ConnectionString));
    }

    // S11
    [Fact]
    public async Task S11_UpgradingFromAddDailyWeeklyRentPeriodsKeepsExistingUsersAndProperties()
    {
        await RebuildFromMigrationsAsync(PreviousMigration);
        Assert.False(await TableExistsAsync(Host.ConnectionString));

        var userId = await SeedUserAsync();
        var propertyId = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO registry.properties (id, type, status, address_country_code, created_at, updated_at) VALUES (@id, 'Villa', 'Active', 'EG', @at, @at)",
            ("id", propertyId),
            ("at", CreatedAt));

        var userBefore = await RowJsonAsync("identity.users", userId);
        var propertyBefore = await RowJsonAsync("registry.properties", propertyId);

        await using (var context = Host.CreateVerificationContext())
        {
            await context.Database.MigrateAsync();
            Assert.Equal(NotificationsMigration, (await context.Database.GetAppliedMigrationsAsync()).Last());
        }

        Assert.Equal(userBefore, await RowJsonAsync("identity.users", userId));
        Assert.Equal(propertyBefore, await RowJsonAsync("registry.properties", propertyId));
        Assert.True(await TableExistsAsync(Host.ConnectionString));
        Assert.Equal(0L, await CountNotificationsAsync());
    }

    // S12
    [Fact]
    public async Task S12_TheMigratedSchemaMatchesAnUntouchedEnsureCreatedReference()
    {
        // This test's database was rebuilt by migrations; the reference is a fresh, untouched template copy.
        var reference = await Fixture.CreateDatabaseAsync();

        try
        {
            Assert.Equal(await ColumnsAsync(reference), await ColumnsAsync(Host.ConnectionString));
            Assert.Equal(await ConstraintDefinitionsAsync(reference), await ConstraintDefinitionsAsync(Host.ConnectionString));
            Assert.Equal(await IndexesAsync(reference), await IndexesAsync(Host.ConnectionString));
        }
        finally
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(reference));
        }
    }

    // S13
    [Fact]
    public async Task S13_StrictDownRefusesEvenWhenTheTableIsEmpty()
    {
        var historyBefore = await HistoryAsync();
        Assert.Equal(0L, await CountNotificationsAsync());

        await AssertStrictDownRefusedAsync();

        Assert.Equal(historyBefore, await HistoryAsync());
        Assert.StartsWith(NotificationsMigration + "|", historyBefore[^1], StringComparison.Ordinal);
        await AssertSchemaAndTableStillPresentAsync();
        Assert.Equal(0L, await CountNotificationsAsync());
    }

    // S14
    [Fact]
    public async Task S14_StrictDownRefusesAndPreservesEveryStoredNotification()
    {
        var recipient = await SeedUserAsync();

        await using (var context = Host.CreateVerificationContext())
        {
            context.Set<Notification>().AddRange(
                Notification.CreateMemberAdded(Guid.NewGuid(), recipient, Guid.NewGuid(), Guid.NewGuid(), CreatedAt),
                Notification.CreateMemberAdded(Guid.NewGuid(), recipient, Guid.NewGuid(), Guid.NewGuid(), CreatedAt.AddSeconds(1)),
                Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, Guid.NewGuid(), CreatedAt.AddSeconds(2)),
                Notification.CreatePasswordChanged(Guid.NewGuid(), recipient, Guid.NewGuid(), CreatedAt.AddSeconds(3)));
            await context.SaveChangesAsync();
        }

        await ExecuteAsync(
            "UPDATE notifications.notifications SET read_at = created_at + interval '1 minute' WHERE created_at IN (@second, @fourth)",
            ("second", CreatedAt.AddSeconds(1)),
            ("fourth", CreatedAt.AddSeconds(3)));

        var historyBefore = await HistoryAsync();
        var rowsBefore = await AllNotificationRowsAsync();
        Assert.Equal(4, rowsBefore.Count);

        await AssertStrictDownRefusedAsync();

        Assert.Equal(historyBefore, await HistoryAsync());
        await AssertSchemaAndTableStillPresentAsync();
        Assert.Equal(rowsBefore, await AllNotificationRowsAsync());
    }

    private async Task AssertStrictDownRefusedAsync()
    {
        await using var context = Host.CreateVerificationContext();

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => context.GetService<IMigrator>().MigrateAsync(PreviousMigration));

        Assert.Equal("55000", exception.SqlState);
        Assert.Equal(StrictDownMessage, exception.MessageText);
        Assert.Equal(StrictDownHint, exception.Hint);
    }

    private async Task AssertSchemaAndTableStillPresentAsync()
    {
        Assert.Equal(
            ["true|true"],
            await RowsAsync(
                Host.ConnectionString,
                "SELECT (to_regnamespace('notifications') IS NOT NULL)::text, (to_regclass('notifications.notifications') IS NOT NULL)::text"));
    }

    // Recreates this test's own database through EF migrations. Pooled connections to it are cleared first, so
    // nothing of the deleted copy is reused, and again afterwards. The shared template is never touched.
    private async Task RebuildFromMigrationsAsync(string? target)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(Host.ConnectionString));

        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using (var migrating = Host.CreateVerificationContext())
        {
            if (target is null)
            {
                await migrating.Database.MigrateAsync();
            }
            else
            {
                await migrating.GetService<IMigrator>().MigrateAsync(target);
            }
        }

        NpgsqlConnection.ClearPool(new NpgsqlConnection(Host.ConnectionString));
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = AccessGraph.ActiveUser($"schema-{Guid.NewGuid():n}@example.test");

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<PostgresException?> TryInsertAsync(
        Guid recipient,
        string type,
        string? subjectType,
        Guid? subjectId,
        short visibilityKind = 1,
        string parameters = "{}",
        DateTimeOffset? readAt = null,
        Guid? occurrenceId = null)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.notifications
                (id, recipient_user_id, type, occurrence_id, visibility_kind, subject_type, subject_id, parameters, created_at, read_at)
            VALUES (@id, @recipient, @type, @occurrence, @kind, @subjectType, @subjectId, @parameters::jsonb, @createdAt, @readAt)
            """,
            connection);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter("recipient", NpgsqlDbType.Uuid) { Value = recipient });
        command.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Varchar) { Value = type });
        command.Parameters.Add(new NpgsqlParameter("occurrence", NpgsqlDbType.Uuid) { Value = occurrenceId ?? Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = visibilityKind });
        command.Parameters.Add(new NpgsqlParameter("subjectType", NpgsqlDbType.Varchar) { Value = (object?)subjectType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("subjectId", NpgsqlDbType.Uuid) { Value = (object?)subjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("parameters", NpgsqlDbType.Text) { Value = parameters });
        command.Parameters.Add(new NpgsqlParameter("createdAt", NpgsqlDbType.TimestampTz) { Value = CreatedAt });
        command.Parameters.Add(new NpgsqlParameter("readAt", NpgsqlDbType.TimestampTz) { Value = (object?)readAt ?? DBNull.Value });

        try
        {
            await command.ExecuteNonQueryAsync();

            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }

    private static void AssertCheckViolation(PostgresException? exception, params string[] acceptedConstraints)
    {
        Assert.NotNull(exception);
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(exception.ConstraintName ?? string.Empty, acceptedConstraints);
    }

    private Task<long> CountNotificationsAsync()
    {
        return ScalarAsync<long>("SELECT count(*) FROM notifications.notifications");
    }

    private Task<string> RowJsonAsync(string table, Guid id)
    {
        // The table name is one of two fixed literals in this file, never input.
        return ScalarAsync<string>($"SELECT row_to_json(t)::text FROM {table} AS t WHERE t.id = @id", ("id", id));
    }

    private Task<IReadOnlyList<string>> HistoryAsync()
    {
        return RowsAsync(
            Host.ConnectionString,
            """SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" COLLATE "C" """);
    }

    private Task<IReadOnlyList<string>> AllNotificationRowsAsync()
    {
        return RowsAsync(
            Host.ConnectionString,
            "SELECT row_to_json(n)::text FROM notifications.notifications AS n ORDER BY n.id");
    }

    private static async Task<bool> TableExistsAsync(string connectionString)
    {
        var rows = await RowsAsync(
            connectionString,
            "SELECT (to_regclass('notifications.notifications') IS NOT NULL)::text");

        return Assert.Single(rows) == "true";
    }

    private static Task<IReadOnlyList<string>> ColumnsAsync(string connectionString)
    {
        return RowsAsync(
            connectionString,
            """
            SELECT column_name, data_type, coalesce(character_maximum_length::text, ''), is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'notifications' AND table_name = 'notifications'
            ORDER BY ordinal_position
            """);
    }

    private static Task<IReadOnlyList<string>> ConstraintDefinitionsAsync(string connectionString)
    {
        return RowsAsync(
            connectionString,
            """
            SELECT conname, contype::text, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = 'notifications.notifications'::regclass
            ORDER BY conname COLLATE "C"
            """);
    }

    private static Task<IReadOnlyList<string>> IndexesAsync(string connectionString)
    {
        return RowsAsync(
            connectionString,
            """
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'notifications' AND tablename = 'notifications'
            ORDER BY indexname COLLATE "C"
            """);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No row returned by: {sql}");

        return reader.GetFieldValue<T>(0);
    }

    // Every column of every row, rendered as text and joined with '|', so whole result sets compare exactly.
    private static async Task<IReadOnlyList<string>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();

        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];

            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index) ? "NULL" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }
}
