#nullable enable
namespace SmartProperty.Api.Infrastructure.Bootstrap;

/// <summary>
/// The <c>Bootstrap</c> configuration section: whether one-time platform provisioning runs at startup, and for
/// whom.
/// </summary>
/// <remarks>
/// Disabled is the default, and it is the default in code as well as in appsettings.json, so a deployment that
/// omits the section entirely provisions nothing. Operations turn it on deliberately, once, and turn it off again
/// afterwards; nothing in the application ever rewrites this configuration.
///
/// It holds no credential. The email identifies an account that must already exist — there is no password,
/// signing key, or token here, and none is accepted.
/// </remarks>
internal sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public bool Enabled { get; set; }

    /// <summary>
    /// The email of an existing registered user to elevate. Normalized by the use case the same way
    /// <c>User</c> normalizes a stored email, so casing and padding do not matter.
    /// </summary>
    public string PlatformAdminEmail { get; set; } = string.Empty;

    /// <summary>
    /// Enabled bootstrap needs an email; disabled bootstrap needs nothing. Validated at startup so a half-set
    /// configuration is caught before the host serves a request, rather than at the moment it would have run.
    /// </summary>
    public bool IsValid()
    {
        return !Enabled || !string.IsNullOrWhiteSpace(PlatformAdminEmail);
    }
}
