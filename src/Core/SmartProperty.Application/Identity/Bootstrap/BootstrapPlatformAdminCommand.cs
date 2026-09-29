using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Identity.Bootstrap;

/// <summary>
/// Provisions the first Platform Administrator by elevating an account that already exists.
/// </summary>
/// <remarks>
/// It takes an email and nothing else. It cannot create an account, a password, or a credential of any kind, so
/// running it can never mint a new identity — it can only grant a platform role to someone who already
/// registered through the normal flow.
/// </remarks>
public sealed record BootstrapPlatformAdminCommand(string PlatformAdminEmail)
    : ICommand<BootstrapPlatformAdminResult>
{
    // The configured email is operator configuration rather than a secret, but it is still personal data, so the
    // record's default ToString is not allowed to scatter it through logs and debugger output.
    public override string ToString()
    {
        return nameof(BootstrapPlatformAdminCommand);
    }
}
