#nullable enable
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Identity.Bootstrap;

namespace SmartProperty.Api.Infrastructure.Bootstrap;

/// <summary>
/// Runs the platform administrator bootstrap once at startup, when configuration explicitly asks for it.
/// </summary>
/// <remarks>
/// Hosting, configuration, and logging live here; the provisioning itself is
/// <see cref="BootstrapPlatformAdminCommandHandler"/>, reached through the same command handler abstraction every
/// endpoint uses. This class decides only <i>whether</i> to run and what to do with the answer.
///
/// When disabled — the default — it touches nothing and startup is unchanged.
///
/// When enabled and the attempt fails, it logs the actionable reason and then throws, which aborts startup. That
/// matches how this application already treats broken startup configuration: a missing
/// <c>ConnectionStrings:Database</c> throws from <c>AddPersistence</c>, and invalid <c>Jwt</c> options fail
/// <c>ValidateOnStart</c>. Continuing quietly would be worse than not starting: an operator who deliberately
/// enabled bootstrap would be left with a platform nobody can administer and no signal that anything went wrong.
///
/// Nothing sensitive is logged. The configured email appears in the log because it is the one fact that makes a
/// failure actionable and it is operator-supplied configuration, not a secret; no password, signing key, database
/// credential, or token is written, and the use case has none to give it.
/// </remarks>
internal sealed class PlatformAdminBootstrapService(
    IOptions<BootstrapOptions> options,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<PlatformAdminBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapOptions = options.Value;

        if (!bootstrapOptions.Enabled)
        {
            logger.LogDebug("Platform administrator bootstrap is disabled. No provisioning was attempted.");

            return;
        }

        logger.LogInformation(
            "Platform administrator bootstrap is enabled for {PlatformAdminEmail}.",
            bootstrapOptions.PlatformAdminEmail);

        // The handler and its repositories are scoped around ApplicationDbContext, and a hosted service is a
        // singleton, so the work runs inside its own scope.
        using var scope = serviceScopeFactory.CreateScope();

        var handler = scope.ServiceProvider
            .GetRequiredService<ICommandHandler<BootstrapPlatformAdminCommand, BootstrapPlatformAdminResult>>();

        var result = await handler.Handle(
            new BootstrapPlatformAdminCommand(bootstrapOptions.PlatformAdminEmail),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!;

            logger.LogCritical(
                "Platform administrator bootstrap failed for {PlatformAdminEmail}: {BootstrapErrorCode} {BootstrapErrorMessage}",
                bootstrapOptions.PlatformAdminEmail,
                error.Code,
                error.Description);

            throw new InvalidOperationException(
                $"Platform administrator bootstrap failed ({error.Code}): {error.Description} "
                + "Resolve the cause, or set Bootstrap__Enabled=false to start without provisioning.");
        }

        var bootstrap = result.Value;

        if (bootstrap.MadeNoChange)
        {
            logger.LogInformation(
                "Platform administrator bootstrap found user {UserId} already provisioned. Nothing was changed. "
                + "Set Bootstrap__Enabled=false to stop running it at startup.",
                bootstrap.UserId);

            return;
        }

        logger.LogWarning(
            "Platform administrator bootstrap provisioned user {UserId} (status {UserStatus}) with role {RoleId}. "
            + "Activated: {UserActivated}. Permission created: {PermissionCreated}. Role created: {RoleCreated}. "
            + "Permission attached: {RolePermissionCreated}. Role assigned: {PlatformRoleAssigned}. "
            + "Set Bootstrap__Enabled=false now that provisioning has succeeded.",
            bootstrap.UserId,
            bootstrap.UserStatus,
            bootstrap.RoleId,
            bootstrap.UserActivated,
            bootstrap.PermissionCreated,
            bootstrap.RoleCreated,
            bootstrap.RolePermissionCreated,
            bootstrap.PlatformRoleAssigned);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
