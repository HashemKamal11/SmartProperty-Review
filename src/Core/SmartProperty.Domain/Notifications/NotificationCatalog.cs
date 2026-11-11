using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace SmartProperty.Domain.Notifications;

/// <summary>
/// The closed set of notification types. Only the types defined here can be created; nothing can register another one
/// at run time. A definition only describes a type: it does not make any business workflow produce it.
/// </summary>
/// <remarks>
/// Type keys are stable and persisted. The frontend renders text from the key and the parameters in the user's
/// selected language, so no display text lives here.
/// </remarks>
public static class NotificationCatalog
{
    /// <summary>An eligible existing user was added to a workspace. Personal; its subject is the workspace.</summary>
    public const string MemberAddedType = "workspace.member_added";

    /// <summary>An account's password was changed through a password reset. Personal; it has no subject.</summary>
    public const string PasswordChangedType = "security.password_changed";

    /// <summary>The subject type that identifies a workspace by its id.</summary>
    public const string WorkspaceSubjectType = "workspace";

    public static NotificationTypeDefinition MemberAdded { get; } = new(
        MemberAddedType,
        NotificationVisibilityKind.Personal,
        WorkspaceSubjectType);

    public static NotificationTypeDefinition PasswordChanged { get; } = new(
        PasswordChangedType,
        NotificationVisibilityKind.Personal,
        subjectType: null);

    /// <summary>Every supported definition, in catalog order.</summary>
    public static ImmutableArray<NotificationTypeDefinition> Definitions { get; } = [MemberAdded, PasswordChanged];

    /// <summary>Every supported type key, in catalog order.</summary>
    public static ImmutableArray<string> ActiveTypes { get; } = [MemberAddedType, PasswordChangedType];

    /// <summary>
    /// Finds the definition for a type key. The comparison is ordinal and case-sensitive; null, empty, unknown and
    /// differently cased keys are not found.
    /// </summary>
    public static bool TryGet(string? type, [NotNullWhen(true)] out NotificationTypeDefinition? definition)
    {
        definition = type switch
        {
            MemberAddedType => MemberAdded,
            PasswordChangedType => PasswordChanged,
            _ => null
        };

        return definition is not null;
    }
}

/// <summary>
/// The fixed description of one notification type. Instances exist only inside <see cref="NotificationCatalog"/>.
/// </summary>
public sealed class NotificationTypeDefinition
{
    internal NotificationTypeDefinition(string type, NotificationVisibilityKind visibility, string? subjectType)
    {
        Type = type;
        Visibility = visibility;
        SubjectType = subjectType;
        AllowedParameterKeys = ImmutableArray<string>.Empty;
    }

    public string Type { get; }

    public NotificationVisibilityKind Visibility { get; }

    /// <summary>The subject type every notification of this type carries, or null when it has no subject.</summary>
    public string? SubjectType { get; }

    /// <summary>The display parameter keys this type accepts. Both current types accept none.</summary>
    public ImmutableArray<string> AllowedParameterKeys { get; }
}
