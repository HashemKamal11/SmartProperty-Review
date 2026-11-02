using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Locations;

namespace SmartProperty.Application.Locations;

/// <summary>
/// Lists one level of the place tree from this platform's own tables, filling them from the AI search service the
/// first time a level is asked for.
/// </summary>
/// <remarks>
/// Read-through: when the places of a level are already stored they are returned without calling the AI service, so
/// browsing keeps working while it is down. Only an empty level triggers a fetch; an AI failure at that moment is
/// returned as the failure it is. The AI service owns the data, so nothing here edits it.
/// </remarks>
public sealed class GetLocationsQueryHandler(
    ILocationRepository locationRepository,
    IAiSearchGateway gateway,
    IUnitOfWork unitOfWork)
    : IQueryHandler<GetLocationsQuery, IReadOnlyList<LocationDetails>>
{
    public static readonly Error ParentNotFound = new(
        "locations.parent_not_found",
        "The parent place was not found.",
        ErrorType.NotFound);

    public async Task<Result<IReadOnlyList<LocationDetails>>> Handle(
        GetLocationsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var level = LocationLevel.Country;
        if (!string.IsNullOrWhiteSpace(query.Level) && !TryParseLevel(query.Level, out level))
        {
            return Failure(Validation(
                $"Level must be one of: {string.Join(", ", Enum.GetNames<LocationLevel>().Select(n => n.ToLowerInvariant()))}."));
        }

        Guid? parentId = null;
        int? parentAiId = null;

        if (level != LocationLevel.Country)
        {
            if (query.ParentId is not { } parent || parent == Guid.Empty)
            {
                return Failure(Validation($"A parent id is required for level '{level.ToString().ToLowerInvariant()}'."));
            }

            parentId = parent;
            parentAiId = await locationRepository.GetAiIdAsync(level - 1, parent, cancellationToken);

            if (parentAiId is null)
            {
                return Failure(ParentNotFound);
            }
        }

        var stored = await locationRepository.ListAsync(level, parentId, cancellationToken);
        if (stored.Count > 0)
        {
            return Result<IReadOnlyList<LocationDetails>>.Success(stored);
        }

        var fetched = await gateway.GetLocationsAsync(level.ToString().ToLowerInvariant(), parentAiId, cancellationToken);
        if (fetched.IsFailure)
        {
            return Failure(fetched.Error!);
        }

        if (fetched.Value.Count == 0)
        {
            return Result<IReadOnlyList<LocationDetails>>.Success([]);
        }

        await locationRepository.AddAsync(level, parentId, fetched.Value, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception) when (exception.Constraint == PersistenceConstraint.LocationAiId)
        {
            // A concurrent request filled the same level first. Its rows are the ones to return.
            unitOfWork.DiscardTrackedChanges();
        }

        return Result<IReadOnlyList<LocationDetails>>.Success(
            await locationRepository.ListAsync(level, parentId, cancellationToken));
    }

    private static bool TryParseLevel(string value, out LocationLevel level)
    {
        level = default;
        var trimmed = value.Trim();

        return Enum.GetNames<LocationLevel>().Any(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(trimmed, ignoreCase: true, out level);
    }

    private static Result<IReadOnlyList<LocationDetails>> Failure(Error error)
    {
        return Result<IReadOnlyList<LocationDetails>>.Failure(error);
    }

    private static Error Validation(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
