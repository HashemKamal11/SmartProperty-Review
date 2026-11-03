using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyMatching;

namespace SmartProperty.Application.PropertyMatching.Search;

public sealed class SearchPropertiesCommandHandler(
    IAiSearchGateway gateway,
    IPropertyRepository propertyRepository,
    IPropertyMatchRepository propertyMatchRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<SearchPropertiesCommand, PropertySearchResult>
{
    /// <summary>A search shows the three best properties unless the caller asks for more.</summary>
    public const int DefaultTopK = 3;

    public const int MaxTopK = 10;

    /// <summary>The AI service refuses a query longer than this.</summary>
    public const int MaxQueryLength = 500;

    private const string NoDescription = "(no description)";

    public async Task<Result<PropertySearchResult>> Handle(
        SearchPropertiesCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Query))
        {
            return Failure(SearchPropertiesErrors.ValidationFailed("Query is required."));
        }

        var query = command.Query.Trim();
        if (query.Length > MaxQueryLength)
        {
            return Failure(SearchPropertiesErrors.ValidationFailed(
                $"Query must not exceed {MaxQueryLength} characters."));
        }

        var topK = command.TopK ?? DefaultTopK;
        if (topK is < 1 or > MaxTopK)
        {
            return Failure(SearchPropertiesErrors.ValidationFailed($"TopK must be between 1 and {MaxTopK}."));
        }

        var search = await gateway.SearchAsync(query, topK, cancellationToken);
        if (search.IsFailure)
        {
            return Failure(search.Error!);
        }

        var outcome = search.Value;

        // The service may know many listings that are not ours. Its own reference (property_id) is never one of our ids;
        // the id this platform sent when it added a listing comes back as external_id. Only a result whose external id
        // is one of our property ids, and which still exists, is a property of this platform.
        var candidates = outcome.Items
            .Select(item => (Item: item, Id: Guid.TryParse(item.ExternalId, out var id) && id != Guid.Empty ? id : (Guid?)null))
            .ToList();

        var candidateIds = candidates.Where(c => c.Id is not null).Select(c => c.Id!.Value).Distinct().ToList();
        var existing = candidateIds.Count == 0
            ? new HashSet<Guid>()
            : (await propertyRepository.GetExistingIdsAsync(candidateIds, cancellationToken)).ToHashSet();

        // A match belongs to a user: an anonymous search (no user id) is answered but never stored.
        Guid? userId = command.UserId is { } id0 && id0 != Guid.Empty ? id0 : null;

        var createdAt = dateTimeProvider.UtcNow;
        var items = new List<PropertySearchItem>(candidates.Count);

        foreach (var (item, id) in candidates)
        {
            var percentage = ToPercentage(item.Score);
            var description = DescriptionOf(item);
            var propertyId = id is { } candidate && existing.Contains(candidate) ? candidate : (Guid?)null;

            if (propertyId is { } ours && userId is { } owner)
            {
                await propertyMatchRepository.AddAsync(
                    new PropertyMatch(
                        Guid.NewGuid(), owner, ours, percentage, description, query, createdAt),
                    cancellationToken);
            }

            items.Add(new PropertySearchItem(propertyId, item.ExternalId, item.PropertyId, percentage, description, item));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<PropertySearchResult>.Success(new PropertySearchResult(
            query,
            outcome.CorrectedQuery,
            outcome.TotalAvailable,
            items));
    }

    /// <summary>The relevance score (0–1) as a percentage, clamped and rounded to two places.</summary>
    internal static decimal ToPercentage(double? score)
    {
        if (score is not { } value || double.IsNaN(value))
        {
            return 0m;
        }

        return Math.Round((decimal)Math.Clamp(value * 100d, 0d, 100d), 2);
    }

    private static string DescriptionOf(AiSearchItem item)
    {
        var text = new[] { item.DescriptionAr, item.DescriptionEn, item.TitleAr, item.TitleEn }
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

        if (text is null)
        {
            return NoDescription;
        }

        var trimmed = text.Trim();

        return trimmed.Length <= PropertyMatch.MaxDescriptionLength
            ? trimmed
            : trimmed[..PropertyMatch.MaxDescriptionLength];
    }

    private static Result<PropertySearchResult> Failure(Error error)
    {
        return Result<PropertySearchResult>.Failure(error);
    }
}
