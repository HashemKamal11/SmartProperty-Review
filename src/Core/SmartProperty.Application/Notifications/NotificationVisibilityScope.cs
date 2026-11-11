using System.Collections.Immutable;
using SmartProperty.Domain.Notifications;

namespace SmartProperty.Application.Notifications;

/// <summary>
/// The closed server-side policy that bounds every notification read and read-state change: one verified recipient,
/// Personal visibility only, and only the types in the deployed <see cref="NotificationCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// A scope is issued only by <see cref="NotificationRecipientGate"/>, after it has checked the authenticated
/// identity against the persisted account. It cannot be built from request input: the constructor is private, the
/// factory is internal, and the visibility kinds and types are fixed rather than parameters, so nothing can widen them.
/// </para>
/// <para>
/// It is a policy object, not a substitute for database authorization. Repositories must enforce the same recipient,
/// current account eligibility, kinds and types inside each statement.
/// </para>
/// </remarks>
public sealed class NotificationVisibilityScope
{
    private static readonly ImmutableArray<NotificationVisibilityKind> PersonalOnly = [NotificationVisibilityKind.Personal];

    private NotificationVisibilityScope(Guid recipientUserId)
    {
        RecipientUserId = recipientUserId;
    }

    /// <summary>The verified recipient, taken from the authenticated identity; never from request input.</summary>
    public Guid RecipientUserId { get; }

    /// <summary>The visibility kinds the scope admits: always exactly <see cref="NotificationVisibilityKind.Personal"/>.</summary>
    public ImmutableArray<NotificationVisibilityKind> VisibilityKinds => PersonalOnly;

    /// <summary>The notification types the scope admits: always exactly <see cref="NotificationCatalog.ActiveTypes"/>.</summary>
    public ImmutableArray<string> Types => NotificationCatalog.ActiveTypes;

    /// <summary>
    /// Creates the scope for a recipient whose identity and account the caller has already verified. Only
    /// <see cref="NotificationRecipientGate"/> calls this in production code.
    /// </summary>
    internal static NotificationVisibilityScope ForVerifiedRecipient(Guid recipientUserId)
    {
        if (recipientUserId == Guid.Empty)
        {
            throw new ArgumentException("The recipient must not be empty.", nameof(recipientUserId));
        }

        return new NotificationVisibilityScope(recipientUserId);
    }
}
