using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Favorites.Remove;

public sealed class RemoveFavoriteCommandHandler(
    IFavoritePropertyRepository favoriteRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RemoveFavoriteCommand, RemoveFavoriteResult>
{
    public async Task<Result<RemoveFavoriteResult>> Handle(
        RemoveFavoriteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<RemoveFavoriteResult>.Failure(FavoriteErrors.ValidationFailed("A signed-in user is required."));
        }

        // The empty id can never be a favorite, so there is nothing to remove and nothing to ask the database.
        if (command.PropertyId == Guid.Empty)
        {
            return Result<RemoveFavoriteResult>.Success(new RemoveFavoriteResult(false));
        }

        var removed = await favoriteRepository.RemoveAsync(command.UserId, command.PropertyId, cancellationToken);

        if (removed)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<RemoveFavoriteResult>.Success(new RemoveFavoriteResult(removed));
    }
}
