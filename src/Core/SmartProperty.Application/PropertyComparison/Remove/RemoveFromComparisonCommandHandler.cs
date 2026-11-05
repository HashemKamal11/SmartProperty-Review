using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyComparison.Remove;

/// <summary>Takes a property off the user's comparison list. Idempotent: no error when it was not on it.</summary>
public sealed record RemoveFromComparisonCommand(Guid UserId, Guid PropertyId) : ICommand<PropertyComparisonDetails>;

/// <summary>Empties the user's comparison list.</summary>
public sealed record ClearComparisonCommand(Guid UserId) : ICommand<PropertyComparisonDetails>;

public sealed class RemoveFromComparisonCommandHandler(
    IPropertyComparisonRepository comparisonRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RemoveFromComparisonCommand, PropertyComparisonDetails>
{
    public async Task<Result<PropertyComparisonDetails>> Handle(
        RemoveFromComparisonCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<PropertyComparisonDetails>.Failure(
                PropertyComparisonErrors.ValidationFailed("A signed-in user is required."));
        }

        if (command.PropertyId != Guid.Empty
            && await comparisonRepository.RemoveAsync(command.UserId, command.PropertyId, cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result<PropertyComparisonDetails>.Success(
            await comparisonRepository.GetAsync(command.UserId, cancellationToken));
    }
}

public sealed class ClearComparisonCommandHandler(
    IPropertyComparisonRepository comparisonRepository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ClearComparisonCommand, PropertyComparisonDetails>
{
    public async Task<Result<PropertyComparisonDetails>> Handle(
        ClearComparisonCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty)
        {
            return Result<PropertyComparisonDetails>.Failure(
                PropertyComparisonErrors.ValidationFailed("A signed-in user is required."));
        }

        await comparisonRepository.ClearAsync(command.UserId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<PropertyComparisonDetails>.Success(
            await comparisonRepository.GetAsync(command.UserId, cancellationToken));
    }
}
