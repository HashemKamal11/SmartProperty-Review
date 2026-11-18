using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.Persistence.Configurations.Notifications;

/// <summary>
/// Maps <see cref="Notification"/> to <c>notifications.notifications</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every SQL literal below is frozen text, deliberately not derived from <see cref="NotificationCatalog"/>: the
/// database rules change only through a reviewed migration, never because the runtime catalog changed. A test compares
/// the frozen type set with the catalog by exact set equality, so a catalog change without a migration fails.
/// </para>
/// <para>
/// The checks enforce the supported row shapes on insert and update. They do not make notification history immutable
/// against arbitrary SQL; supported read-state transitions are enforced by the repository's statement predicates.
/// </para>
/// </remarks>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public const string SchemaName = "notifications";
    public const string TableName = "notifications";

    public const string PrimaryKeyName = "PK_notifications";
    public const string RecipientForeignKeyName = "FK_notifications_users_recipient_user_id";

    public const string TypeCheckName = "ck_notifications_type";
    public const string VisibilityKindCheckName = "ck_notifications_visibility_kind";
    public const string ParametersCheckName = "ck_notifications_parameters";
    public const string ReadAfterCreatedCheckName = "ck_notifications_read_after_created";
    public const string SubjectCheckName = "ck_notifications_subject";

    /// <summary>One notification per recipient, type and business occurrence.</summary>
    public const string OccurrenceUniqueIndexName = "ux_notifications_recipient_type_occurrence";

    /// <summary>A recipient's list, newest first.</summary>
    public const string RecipientCreatedIndexName = "ix_notifications_recipient_created";

    /// <summary>A recipient's unread notifications, newest first: the unread list and the unread count.</summary>
    public const string RecipientUnreadIndexName = "ix_notifications_recipient_unread";

    internal const string TypeCheckSql =
        "type IN ('workspace.member_added', 'security.password_changed')";

    internal const string VisibilityKindCheckSql = "visibility_kind = 1";

    internal const string ParametersCheckSql = "parameters = '{}'::jsonb";

    internal const string ReadAfterCreatedCheckSql = "read_at IS NULL OR read_at >= created_at";

    // NULL-safe: a NULL comparison inside either branch makes that branch false rather than UNKNOWN, and COALESCE turns
    // any remaining UNKNOWN into false, so a CHECK can never pass by evaluating to NULL. Unknown types fail as well.
    internal const string SubjectCheckSql =
        "COALESCE("
        + "(type = 'workspace.member_added' AND subject_type IS NOT DISTINCT FROM 'workspace' AND subject_id IS NOT NULL)"
        + " OR "
        + "(type = 'security.password_changed' AND subject_type IS NULL AND subject_id IS NULL)"
        + ", false)";

    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> ParametersComparer = new(
        (left, right) => ParametersEqual(left, right),
        value => ParametersHashCode(value),
        value => ParametersSnapshot(value));

    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable(TableName, SchemaName, table =>
        {
            table.HasCheckConstraint(TypeCheckName, TypeCheckSql);
            table.HasCheckConstraint(VisibilityKindCheckName, VisibilityKindCheckSql);
            table.HasCheckConstraint(ParametersCheckName, ParametersCheckSql);
            table.HasCheckConstraint(ReadAfterCreatedCheckName, ReadAfterCreatedCheckSql);
            table.HasCheckConstraint(SubjectCheckName, SubjectCheckSql);
        });

        builder.HasKey(notification => notification.Id)
            .HasName(PrimaryKeyName);

        // The id is always the caller's value; the database never generates one.
        builder.Property(notification => notification.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(notification => notification.RecipientUserId)
            .HasColumnName("recipient_user_id")
            .IsRequired();

        builder.Property(notification => notification.Type)
            .HasColumnName("type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(notification => notification.OccurrenceId)
            .HasColumnName("occurrence_id")
            .IsRequired();

        builder.Property(notification => notification.VisibilityKind)
            .HasColumnName("visibility_kind")
            .HasColumnType("smallint")
            .IsRequired();

        builder.Property(notification => notification.SubjectType)
            .HasColumnName("subject_type")
            .HasMaxLength(32);

        builder.Property(notification => notification.SubjectId)
            .HasColumnName("subject_id");

        builder.Property(notification => notification.Parameters)
            .HasColumnName("parameters")
            .HasColumnType("jsonb")
            .HasConversion(
                value => WriteParameters(value),
                json => ReadParameters(json),
                ParametersComparer)
            .IsRequired();

        builder.Property(notification => notification.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(notification => notification.ReadAt)
            .HasColumnName("read_at");

        // Users are deactivated, never deleted, so nothing cascades: a delete that would orphan notifications is refused.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(notification => notification.RecipientUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(RecipientForeignKeyName);

        builder.HasIndex(
                notification => new { notification.RecipientUserId, notification.Type, notification.OccurrenceId },
                OccurrenceUniqueIndexName)
            .IsUnique()
            .HasDatabaseName(OccurrenceUniqueIndexName);

        builder.HasIndex(
                notification => new { notification.RecipientUserId, notification.CreatedAt, notification.Id },
                RecipientCreatedIndexName)
            .IsDescending(false, true, true)
            .HasDatabaseName(RecipientCreatedIndexName);

        builder.HasIndex(
                notification => new { notification.RecipientUserId, notification.CreatedAt, notification.Id },
                RecipientUnreadIndexName)
            .IsDescending(false, true, true)
            .HasFilter("read_at IS NULL")
            .HasDatabaseName(RecipientUnreadIndexName);
    }

    /// <summary>Writes the parameters as a JSON object with keys in ordinal order, so equal content gives equal text.</summary>
    internal static string WriteParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in parameters)
        {
            ordered.Add(key, value ?? throw new InvalidOperationException("Notification parameter values must not be null."));
        }

        return JsonSerializer.Serialize(ordered);
    }

    /// <summary>
    /// Reads a JSON object of string values into an immutable dictionary with ordinal keys. Anything else is refused
    /// rather than guessed at.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadParameters(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Notification parameters must be a JSON object.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var member in root.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Notification parameter values must be JSON strings.");
            }

            if (builder.ContainsKey(member.Name))
            {
                throw new InvalidOperationException("Notification parameters must not repeat a key.");
            }

            builder.Add(member.Name, member.Value.GetString()!);
        }

        return builder.ToImmutable();
    }

    /// <summary>Equal when both hold the same keys with the same values, compared ordinally, in any order.</summary>
    internal static bool ParametersEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        var leftPairs = left.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var rightPairs = right.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();

        for (var index = 0; index < leftPairs.Length; index++)
        {
            if (!string.Equals(leftPairs[index].Key, rightPairs[index].Key, StringComparison.Ordinal)
                || !string.Equals(leftPairs[index].Value, rightPairs[index].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Combines each pair's ordinal hash with XOR, so the result does not depend on enumeration order.</summary>
    internal static int ParametersHashCode(IReadOnlyDictionary<string, string> parameters)
    {
        var combined = 0;

        foreach (var (key, value) in parameters)
        {
            combined ^= HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key),
                StringComparer.Ordinal.GetHashCode(value));
        }

        return HashCode.Combine(parameters.Count, combined);
    }

    internal static IReadOnlyDictionary<string, string> ParametersSnapshot(IReadOnlyDictionary<string, string> parameters)
    {
        return parameters.ToImmutableDictionary(StringComparer.Ordinal);
    }
}
