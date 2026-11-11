using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.Requests.Settings;

/// <summary>Reads the signing window that applies to the next acceptance.</summary>
public sealed record GetSigningWindowQuery : IQuery<SigningWindowResult>;

/// <summary>Sets how many hours the parties have, from acceptance, to sign before an unsigned commitment expires.</summary>
public sealed record SetSigningWindowCommand(int Hours) : ICommand<SigningWindowResult>;

/// <summary>
/// The window in hours, and whether an administrator has set it (otherwise it is the default).
/// </summary>
public sealed record SigningWindowResult(int Hours, bool IsDefault, DateTimeOffset? UpdatedAt);

/// <summary>
/// The administrator's control over signing deadlines. Both handlers need the Platform-scoped
/// <see cref="PermissionCodes.MarketplaceSettingsManage"/> permission, checked here as well as at the endpoint.
/// </summary>
/// <remarks>
/// A change applies to acceptances made after it. A deadline already given to a commitment is stored on that
/// commitment and never moves.
/// </remarks>
public sealed class SigningWindowHandlers(
    ICurrentUser currentUser,
    IUserRepository users,
    IPermissionChecker permissions,
    IMarketplaceSettingsRepository settings,
    IDateTimeProvider clock)
    : IQueryHandler<GetSigningWindowQuery, SigningWindowResult>,
      ICommandHandler<SetSigningWindowCommand, SigningWindowResult>
{
    public async Task<Result<SigningWindowResult>> Handle(GetSigningWindowQuery query, CancellationToken cancellationToken = default)
    {
        var admin = await AuthorizeAsync(cancellationToken);

        if (admin.Error is { } error)
        {
            return Result<SigningWindowResult>.Failure(error);
        }

        return Result<SigningWindowResult>.Success(await CurrentAsync(cancellationToken));
    }

    public async Task<Result<SigningWindowResult>> Handle(SetSigningWindowCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admin = await AuthorizeAsync(cancellationToken);

        if (admin.Error is { } error)
        {
            return Result<SigningWindowResult>.Failure(error);
        }

        if (command.Hours is < MarketplaceSettings.MinSigningWindowHours or > MarketplaceSettings.MaxSigningWindowHours)
        {
            return Result<SigningWindowResult>.Failure(ListingSubmissionErrors.ValidationFailed(
                $"The signing window is {MarketplaceSettings.MinSigningWindowHours} to {MarketplaceSettings.MaxSigningWindowHours} hours."));
        }

        await settings.SaveSigningWindowAsync(command.Hours, admin.UserId, clock.UtcNow, cancellationToken);

        return Result<SigningWindowResult>.Success(await CurrentAsync(cancellationToken));
    }

    private async Task<SigningWindowResult> CurrentAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(cancellationToken);

        return stored is null
            ? new SigningWindowResult(MarketplaceSettings.DefaultSigningWindowHours, true, null)
            : new SigningWindowResult(stored.SigningWindowHours, false, stored.UpdatedAt);
    }

    private async Task<ActingWriter> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is not null)
        {
            return acting;
        }

        var decision = await permissions.CheckAsync(
            AuthorizationRequest.For(acting.UserId, PermissionCodes.MarketplaceSettingsManage, AuthorizationTarget.Platform),
            cancellationToken);

        // Only an explicit allow passes; a checker failure propagates and is never turned into access.
        return decision.IsAllowed
            ? acting
            : new ActingWriter(Guid.Empty, new Error(
                "authorization.forbidden", "You do not have permission to access this resource.", ErrorType.Forbidden));
    }
}
