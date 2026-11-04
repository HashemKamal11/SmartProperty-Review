namespace SmartProperty.Domain.PropertyRegistration;

/// <summary>
/// Immutable record of why, when, and by whom a registration request was withdrawn. Withdrawal is irreversible.
/// </summary>
public sealed class PropertyRegistrationRequestWithdrawal
{
    private PropertyRegistrationRequestWithdrawal()
    {
        Reason = string.Empty;
    }

    internal PropertyRegistrationRequestWithdrawal(Guid withdrawnByUserId, string reason, DateTimeOffset withdrawnAt)
    {
        WithdrawnByUserId = PropertyRegistrationGuard.RequireId(withdrawnByUserId, nameof(withdrawnByUserId));
        Reason = PropertyRegistrationGuard.RequireText(reason, nameof(reason));
        WithdrawnAt = withdrawnAt;
    }

    public Guid WithdrawnByUserId { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset WithdrawnAt { get; private set; }
}
