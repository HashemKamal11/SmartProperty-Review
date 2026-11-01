using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyMatching;

namespace SmartProperty.Application.PropertyMatching.Record;

public sealed class RecordPropertyMatchCommandHandler(
    IUserRepository userRepository,
    IPropertyRepository propertyRepository,
    IPropertyMatchRepository propertyMatchRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RecordPropertyMatchCommand, PropertyMatchDetails>
{
    public async Task<Result<PropertyMatchDetails>> Handle(
        RecordPropertyMatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId is null || command.UserId == Guid.Empty)
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed("User id is required."));
        }

        if (command.PropertyId is null || command.PropertyId == Guid.Empty)
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed("Property id is required."));
        }

        if (command.MatchPercentage is null or < 0m or > 100m)
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed(
                "Match percentage is required and must be between 0 and 100."));
        }

        if (string.IsNullOrWhiteSpace(command.Description))
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed("Description is required."));
        }

        if (command.Description.Trim().Length > PropertyMatch.MaxDescriptionLength)
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed(
                $"Description must not exceed {PropertyMatch.MaxDescriptionLength} characters."));
        }

        if (string.IsNullOrWhiteSpace(command.Query))
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed("Query is required."));
        }

        if (command.Query.Trim().Length > PropertyMatch.MaxQueryLength)
        {
            return Failure(RecordPropertyMatchErrors.ValidationFailed(
                $"Query must not exceed {PropertyMatch.MaxQueryLength} characters."));
        }

        if (await userRepository.GetByIdAsync(command.UserId.Value, cancellationToken) is null)
        {
            return Failure(RecordPropertyMatchErrors.UserNotFound);
        }

        if (await propertyRepository.GetDetailsByIdAsync(command.PropertyId.Value, cancellationToken) is null)
        {
            return Failure(RecordPropertyMatchErrors.PropertyNotFound);
        }

        var propertyMatch = new PropertyMatch(
            Guid.NewGuid(),
            command.UserId.Value,
            command.PropertyId.Value,
            command.MatchPercentage.Value,
            command.Description,
            command.Query,
            dateTimeProvider.UtcNow);

        await propertyMatchRepository.AddAsync(propertyMatch, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<PropertyMatchDetails>.Success(new PropertyMatchDetails(
            propertyMatch.Id,
            propertyMatch.UserId,
            propertyMatch.PropertyId,
            propertyMatch.MatchPercentage,
            propertyMatch.Description,
            propertyMatch.Query,
            propertyMatch.CreatedAt));
    }

    private static Result<PropertyMatchDetails> Failure(Error error)
    {
        return Result<PropertyMatchDetails>.Failure(error);
    }
}
