using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Favorites;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Favorites.Add;

public sealed class AddFavoriteCommandHandler(
    IUserRepository userRepository,
    IPropertyRepository propertyRepository,
    IFavoritePropertyRepository favoriteRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<AddFavoriteCommand, AddFavoriteResult>
{
    public async Task<Result<AddFavoriteResult>> Handle(
        AddFavoriteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Failure(FavoriteErrors.ValidationFailed("A signed-in user is required."));
        }

        // No property can have the empty id, so asking the database would only confirm what is already known.
        if (command.PropertyId == Guid.Empty)
        {
            return Failure(FavoriteErrors.PropertyNotFound);
        }

        if (await userRepository.GetByIdAsync(command.UserId, cancellationToken) is null)
        {
            return Failure(FavoriteErrors.UserNotFound);
        }

        // Already a favorite is the answer whatever has happened to the property since: the user keeps it.
        if (await favoriteRepository.FindAsync(command.UserId, command.PropertyId, cancellationToken) is { } existing)
        {
            return Success(existing, created: false);
        }

        var property = await propertyRepository.GetDetailsByIdAsync(command.PropertyId, cancellationToken);

        if (property is null)
        {
            return Failure(FavoriteErrors.PropertyNotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Failure(FavoriteErrors.PropertyArchived);
        }

        var favorite = new FavoriteProperty(command.UserId, command.PropertyId, dateTimeProvider.UtcNow);

        await favoriteRepository.AddAsync(favorite, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.FavoriteProperty)
        {
            // A concurrent request marked the same property first. The favorite exists, which is what was asked.
            unitOfWork.DiscardTrackedChanges();

            var winner = await favoriteRepository.FindAsync(command.UserId, command.PropertyId, cancellationToken);

            return winner is null ? Failure(FavoriteErrors.PropertyNotFound) : Success(winner, created: false);
        }

        return Success(favorite, created: true);
    }

    private static Result<AddFavoriteResult> Success(FavoriteProperty favorite, bool created)
    {
        return Result<AddFavoriteResult>.Success(new AddFavoriteResult(favorite.PropertyId, favorite.CreatedAt, created));
    }

    private static Result<AddFavoriteResult> Failure(Error error)
    {
        return Result<AddFavoriteResult>.Failure(error);
    }
}
