namespace SmartProperty.Application.Authorization;

/// <summary>
/// The outcome of an authorization question: allowed, or not.
/// </summary>
/// <remarks>
/// Deliberately carries no reason, code, role, or permission detail. Every denial looks the same, which is what
/// the public <c>403 authorization.forbidden</c> response requires; a caller that could read why it was denied
/// could map out the access model. A denial means the answer is no, not that something went wrong — an
/// unexpected infrastructure failure must surface as an exception, never as <see cref="Denied"/>.
/// </remarks>
public sealed record AuthorizationDecision
{
    private AuthorizationDecision(bool isAllowed)
    {
        IsAllowed = isAllowed;
    }

    public static AuthorizationDecision Allowed { get; } = new(isAllowed: true);

    public static AuthorizationDecision Denied { get; } = new(isAllowed: false);

    public bool IsAllowed { get; }

    public override string ToString()
    {
        return IsAllowed ? nameof(Allowed) : nameof(Denied);
    }
}
