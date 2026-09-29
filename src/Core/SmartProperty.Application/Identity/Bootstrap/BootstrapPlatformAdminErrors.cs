using SmartProperty.Common.Results;

namespace SmartProperty.Application.Identity.Bootstrap;

internal static class BootstrapPlatformAdminErrors
{
    public static readonly Error PlatformAdminEmailMissing = new(
        "bootstrap.platform_admin_email_missing",
        "Bootstrap is enabled but no platform administrator email is configured. "
        + "Set Bootstrap__PlatformAdminEmail to the email of an existing registered user.",
        ErrorType.Validation);

    // Deliberately actionable, and deliberately not "so one will be created": bootstrap elevates an existing
    // account and never creates an identity or a credential.
    public static readonly Error PlatformAdminNotFound = new(
        "bootstrap.platform_admin_not_found",
        "No registered user matches the configured platform administrator email. "
        + "Register that account first, then run bootstrap again.",
        ErrorType.NotFound);

    // Suspended and Deactivated are intentional account-level decisions. Bootstrap refuses rather than
    // overriding one, because reviving a disabled account and handing it platform authority is exactly the
    // escalation a bootstrap mechanism must not offer.
    public static readonly Error PlatformAdminAccountUnavailable = new(
        "bootstrap.platform_admin_account_unavailable",
        "The configured platform administrator account is suspended or deactivated. "
        + "Bootstrap will not re-enable it; resolve the account status first.",
        ErrorType.Conflict);
}
