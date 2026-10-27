namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// Whether a property authority grant is still in force. Revocation is permanent: a revoked grant never becomes
/// active again, a new grant has to be issued.
/// </summary>
public enum AuthorityGrantState
{
    Active = 1,
    Revoked = 2
}
