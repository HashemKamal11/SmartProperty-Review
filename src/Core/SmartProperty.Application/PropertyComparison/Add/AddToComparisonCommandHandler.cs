using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyComparison.Add;

/// <summary>
/// Puts a property on the user's comparison list. Idempotent: a property already on the list is reported as such and
/// nothing changes.
/// </summary>
public sealed record AddToComparisonCommand(Guid UserId, Guid PropertyId) : ICommand<AddToComparisonResult>;

/// <param name="Created">True when this call added the property; false when it was already there.</param>
public sealed record AddToComparisonResult(bool Created, PropertyComparisonDetails Comparison);

public sealed class AddToComparisonCommandHandler(
    IUserRepository userRepository,
    IPropertyRepository propertyRepository,
    IPropertyComparisonRepository comparisonRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<AddToComparisonCommand, AddToComparisonResult>
{
    public async Task<Result<AddToComparisonResult>> Handle(
        AddToComparisonCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Failure(PropertyComparisonErrors.ValidationFailed("A signed-in user is required."));
        }

        // No property can have the empty id, so asking the database would only confirm what is already known.
        if (command.PropertyId == Guid.Empty)
        {
            return Failure(PropertyComparisonErrors.PropertyNotFound);
        }

        if (await userRepository.GetByIdAsync(command.UserId, cancellationToken) is null)
        {
            return Failure(PropertyComparisonErrors.UserNotFound);
        }

        var items = await comparisonRepository.ListItemsAsync(command.UserId, cancellationToken);

        // Already on the list is the answer whatever has happened to the property since: the user keeps it.
        if (items.Any(item => item.PropertyId == command.PropertyId))
        {
            return Success(created: false, await comparisonRepository.GetAsync(command.UserId, cancellationToken));
        }

        var property = await propertyRepository.GetDetailsByIdAsync(command.PropertyId, cancellationToken);

        if (property is null)
        {
            return Failure(PropertyComparisonErrors.PropertyNotFound);
        }

        if (property.Status != PropertyStatus.Active)
        {
            return Failure(PropertyComparisonErrors.PropertyArchived);
        }

        // The comparison holds physical properties. Whether and how a property is currently advertised is a listing
        // fact, so it neither gates nor splits the list.
        if (items.Count >= PropertyComparisonItem.MaxItems)
        {
            return Failure(PropertyComparisonErrors.LimitReached);
        }

        // The first free position. Two requests racing for the same one collide on the unique position, and the loser
        // is told to retry rather than ending up with a fourth property.
        var taken = items.Select(item => item.Position).ToHashSet();
        var position = Enumerable.Range(1, PropertyComparisonItem.MaxItems).First(candidate => !taken.Contains(candidate));

        await comparisonRepository.AddAsync(
            new PropertyComparisonItem(command.UserId, command.PropertyId, position, dateTimeProvider.UtcNow),
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.PropertyComparisonItem)
        {
            // A concurrent request put the same property on the list first. It is there, which is what was asked.
            unitOfWork.DiscardTrackedChanges();

            return Success(created: false, await comparisonRepository.GetAsync(command.UserId, cancellationToken));
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.PropertyComparisonPosition)
        {
            unitOfWork.DiscardTrackedChanges();

            return Failure(PropertyComparisonErrors.ConcurrentChange);
        }

        return Success(created: true, await comparisonRepository.GetAsync(command.UserId, cancellationToken));
    }

    private static Result<AddToComparisonResult> Success(bool created, PropertyComparisonDetails comparison)
    {
        return Result<AddToComparisonResult>.Success(new AddToComparisonResult(created, comparison));
    }

    private static Result<AddToComparisonResult> Failure(Error error)
    {
        return Result<AddToComparisonResult>.Failure(error);
    }
}
