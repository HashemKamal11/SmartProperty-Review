using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyMatching;

namespace SmartProperty.Application.PropertyMatching.Search;

public sealed class SearchPropertiesCommandHandler(
    IAiSearchGateway gateway,
    IPropertyRepository propertyRepository,
    IPropertyMatchRepository propertyMatchRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork,
    IPropertyListingsLookup listings)
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

        // The service holds advertisements. The id this platform sent when it added one, the external id, is the LISTING
        // id; the service's own reference is never one of our ids. Only a result whose external id is a listing that is
        // visible right now is ours, and it maps to the property it advertises. A stale record (a withdrawn, closed or
        // unapproved listing, or one indexed before listings existed, whose external id was a property id) maps to
        // nothing: it is shown as a result of the service, never matched to a property and never stored.
        var candidates = outcome.Items
            .Select(item => (Item: item, Id: Guid.TryParse(item.ExternalId, out var id) && id != Guid.Empty ? id : (Guid?)null))
            .ToList();

        var candidateIds = candidates.Where(c => c.Id is not null).Select(c => c.Id!.Value).Distinct().ToList();
        var ours = candidateIds.Count == 0
            ? new Dictionary<Guid, PropertyListingReference>()
            : (await listings.GetByListingIdsAsync(candidateIds, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var existing = ours.Values.Select(reference => reference.PropertyId).ToHashSet();

        // The property itself, in the shape GET /api/properties/{id} returns, for each result that is one of ours.
        var ownDetails = existing.Count == 0
            ? new Dictionary<Guid, PropertyDetails>()
            : (await propertyRepository.GetDetailsByIdsAsync(existing.ToList(), cancellationToken))
                .ToDictionary(details => details.Id);

        // A match belongs to a user: an anonymous search (no user id) is answered but never stored.
        Guid? userId = command.UserId is { } id0 && id0 != Guid.Empty ? id0 : null;

        var createdAt = dateTimeProvider.UtcNow;
        var items = new List<PropertySearchItem>(candidates.Count);

        foreach (var (item, id) in candidates)
        {
            var percentage = ToPercentage(item.Score);
            var description = DescriptionOf(item);
            var reference = id is { } candidate && ours.TryGetValue(candidate, out var found0) ? found0 : null;
            var propertyId = reference?.PropertyId;

            if (propertyId is { } matched && userId is { } owner)
            {
                await propertyMatchRepository.AddAsync(
                    new PropertyMatch(
                        Guid.NewGuid(), owner, matched, percentage, description, query, createdAt),
                    cancellationToken);
            }

            items.Add(new PropertySearchItem(
                propertyId,
                item.ExternalId,
                item.PropertyId,
                percentage,
                description,
                item,
                propertyId is { } found && ownDetails.TryGetValue(found, out var details) ? details : null,
                reference?.Listing));
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
