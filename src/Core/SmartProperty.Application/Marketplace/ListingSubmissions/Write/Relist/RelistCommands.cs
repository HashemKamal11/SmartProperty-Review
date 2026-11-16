using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;

/// <summary>
/// The owner brings their closed listing back: a copy of its last approved version is submitted for review.
/// </summary>
public sealed record RelistListingCommand(Guid ListingId) : ICommand<RelistListingResult>;

/// <summary>Sets what happens to the caller's own listing once an accepted request that closed it ends without a deal.</summary>
public sealed record SetRelistModeCommand(Guid ListingId, RelistMode Mode) : ICommand<RelistModeResult>;

public sealed record RelistModeResult(Guid ListingId, RelistMode Mode);

/// <summary>See <see cref="ListingRelister"/>.</summary>
public sealed class RelistListingCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    ListingRelister relister)
    : ICommandHandler<RelistListingCommand, RelistListingResult>
{
    public async Task<Result<RelistListingResult>> Handle(
        RelistListingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RelistListingResult>.Failure(accountError);
        }

        return await relister.RelistByOwnerAsync(acting.UserId, command.ListingId, cancellationToken);
    }
}

/// <summary>
/// Records the owner's automatic relisting choice on their listing.
/// </summary>
/// <remarks>
/// The caller must be the author of the listing's latest version; anything else is the same not-found. A grant in
/// force is not required to record a preference: relisting itself checks the grant when it happens. The listing's
/// state and history are untouched.
/// </remarks>
public sealed class SetRelistModeCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IListingSubmissionWriteRepository repository)
    : ICommandHandler<SetRelistModeCommand, RelistModeResult>
{
    public async Task<Result<RelistModeResult>> Handle(
        SetRelistModeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = await ActingWriter.ResolveAsync(currentUser, users, cancellationToken);

        if (acting.Error is { } accountError)
        {
            return Result<RelistModeResult>.Failure(accountError);
        }

        if (!Enum.IsDefined(command.Mode))
        {
            return Result<RelistModeResult>.Failure(ListingSubmissionErrors.ValidationFailed("The relist mode is not supported."));
        }

        var known = await repository.FindListingAsync(command.ListingId, cancellationToken);

        if (known is null)
        {
            return Result<RelistModeResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        var property = await repository.LockPropertyAsync(known.PropertyId, cancellationToken);

        if (property is null || property.PropertyId != known.PropertyId)
        {
            return Result<RelistModeResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        // Read only to take the locks in order; the grant does not decide a preference.
        await repository.GetGrantForSharedLockAsync(acting.UserId, known.PropertyId, known.Purpose, cancellationToken);

        var listing = await repository.GetListingByIdAsync(command.ListingId, cancellationToken);
        var latest = listing is null ? null : await repository.GetLatestSubmissionAsync(listing.Id, cancellationToken);

        if (listing is null || latest is null || latest.Authority.ActingUserId != acting.UserId)
        {
            return Result<RelistModeResult>.Failure(ListingSubmissionErrors.NotFound);
        }

        listing.SetRelistAutomatically(command.Mode == RelistMode.Automatic);

        if (await WriteCommit.SaveAsync(repository, WriteCommit.ForChange, cancellationToken) is { } conflict)
        {
            return Result<RelistModeResult>.Failure(conflict);
        }

        return Result<RelistModeResult>.Success(new RelistModeResult(listing.Id, listing.RelistAutomatically ? RelistMode.Automatic : RelistMode.Manual));
    }
}
