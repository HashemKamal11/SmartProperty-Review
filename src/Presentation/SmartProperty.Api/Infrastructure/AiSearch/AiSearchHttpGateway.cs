#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Common.Results;

namespace SmartProperty.Api.Infrastructure.AiSearch;

/// <summary>
/// Talks to the AI search service over HTTP. Every failure of the service comes back as a failed result; only a
/// cancellation requested by the caller propagates.
/// </summary>
internal sealed class AiSearchHttpGateway(
    HttpClient httpClient,
    IOptions<AiSearchOptions> options,
    ILogger<AiSearchHttpGateway> logger)
    : IAiSearchGateway
{
    private const string ApiKeyHeader = "x-api-key";
    private const int MaxDetailLength = 300;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly AiSearchOptions _options = options.Value;

    public async Task<Result<AiListingAcknowledgement>> PutListingAsync(
        string externalId,
        AiListing listing,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<AiListingAcknowledgement>.Failure(AiSearchErrors.Disabled);
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"v1/listings/{Uri.EscapeDataString(externalId)}")
        {
            Content = JsonContent.Create(listing, options: Json)
        };
        AddApiKey(request);

        var outcome = await SendAsync(request, cancellationToken);
        if (outcome.Failure is { } failure)
        {
            return Result<AiListingAcknowledgement>.Failure(failure);
        }

        using var response = outcome.Response!;

        return Result<AiListingAcknowledgement>.Success(
            new AiListingAcknowledgement(response.StatusCode == HttpStatusCode.Created));
    }

    public async Task<Result<bool>> DeleteListingAsync(
        string externalId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<bool>.Failure(AiSearchErrors.Disabled);
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1/listings/{Uri.EscapeDataString(externalId)}");
        AddApiKey(request);

        // A listing the service never held is already removed, which is all the caller wanted.
        var outcome = await SendAsync(request, cancellationToken, acceptNotFound: true);
        if (outcome.Failure is { } failure)
        {
            return Result<bool>.Failure(failure);
        }

        using var response = outcome.Response!;

        return Result<bool>.Success(response.StatusCode != HttpStatusCode.NotFound);
    }

    public async Task<Result<AiSearchOutcome>> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<AiSearchOutcome>.Failure(AiSearchErrors.Disabled);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/search")
        {
            Content = JsonContent.Create(new SearchBody(query, topK), options: Json)
        };

        var outcome = await SendAsync(request, cancellationToken);
        if (outcome.Failure is { } failure)
        {
            return Result<AiSearchOutcome>.Failure(failure);
        }

        using var response = outcome.Response!;

        try
        {
            var body = await response.Content.ReadFromJsonAsync<SearchResponseBody>(Json, cancellationToken);

            if (body is null)
            {
                return Result<AiSearchOutcome>.Failure(AiSearchErrors.Unavailable("the search answer was empty"));
            }

            return Result<AiSearchOutcome>.Success(new AiSearchOutcome(
                body.Query ?? query,
                body.CorrectedQuery ?? query,
                body.TotalAvailable,
                (body.Items ?? []).Where(item => !string.IsNullOrWhiteSpace(item.PropertyId)).Select(ToItem).ToList()));
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "The AI search service returned a search answer that could not be read.");

            return Result<AiSearchOutcome>.Failure(AiSearchErrors.Unavailable("the search answer was not understood"));
        }
    }

    public async Task<Result<IReadOnlyList<AiPlace>>> GetLocationsAsync(
        string level,
        int? parentId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<IReadOnlyList<AiPlace>>.Failure(AiSearchErrors.Disabled);
        }

        var url = $"v1/locations?level={Uri.EscapeDataString(level)}"
            + (parentId is { } parent ? $"&parent_id={parent}" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var outcome = await SendAsync(request, cancellationToken);
        if (outcome.Failure is { } failure)
        {
            return Result<IReadOnlyList<AiPlace>>.Failure(failure);
        }

        using var response = outcome.Response!;

        try
        {
            var places = await response.Content.ReadFromJsonAsync<List<PlaceBody>>(Json, cancellationToken) ?? [];

            return Result<IReadOnlyList<AiPlace>>.Success(
                places.Select(place => new AiPlace(place.Id, place.NameEn ?? string.Empty, place.NameAr ?? string.Empty))
                    .ToList());
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "The AI search service returned a place list that could not be read.");

            return Result<IReadOnlyList<AiPlace>>.Failure(AiSearchErrors.Unavailable("the place list was not understood"));
        }
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.ApiKey);
        }
    }

    /// <summary>Sends the request and classifies the answer; on a failure there is no response to dispose.</summary>
    private async Task<(HttpResponseMessage? Response, Error? Failure)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool acceptNotFound = false)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client timeout fired; the caller did not cancel.
            logger.LogWarning("The AI search service did not answer {Method} {Path} in time.", request.Method, request.RequestUri);

            return (null, AiSearchErrors.Unavailable("it did not answer in time"));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "The AI search service could not be reached for {Method} {Path}.", request.Method, request.RequestUri);

            return (null, AiSearchErrors.Unavailable("it could not be reached"));
        }

        if (response.IsSuccessStatusCode || (acceptNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return (response, null);
        }

        using (response)
        {
            var detail = await ReadDetailAsync(response, cancellationToken);

            logger.LogWarning(
                "The AI search service answered {Status} to {Method} {Path}: {Detail}",
                (int)response.StatusCode,
                request.Method,
                request.RequestUri,
                detail);

            return (null, response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                    AiSearchErrors.Rejected(detail),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    AiSearchErrors.Unavailable("it refused the API key (check AiSearch:ApiKey)"),
                _ => AiSearchErrors.Unavailable($"it answered {(int)response.StatusCode}")
            });
        }
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

            return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength];
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return response.ReasonPhrase ?? "no detail";
        }
    }

    private static AiSearchItem ToItem(ItemBody item)
    {
        return new AiSearchItem(
            item.PropertyId!,
            item.ExternalId,
            item.Score,
            item.City,
            item.CityAr,
            item.Area,
            item.AreaAr,
            item.District,
            item.DistrictAr,
            item.Compound,
            item.CompoundAr,
            item.PropertyType,
            item.PropertyTypeAr,
            item.TransactionType,
            item.TransactionTypeAr,
            item.Price,
            item.Currency,
            item.AreaSqm,
            item.Bedrooms,
            item.Bathrooms,
            item.FinishingStatus,
            item.Furnished,
            item.View,
            item.Amenities,
            item.ListingStatus,
            item.TitleAr,
            item.TitleEn,
            item.DescriptionAr,
            item.DescriptionEn);
    }

    private sealed record SearchBody(string Query, int TopK);

    private sealed record PlaceBody(int Id, string? NameEn, string? NameAr);

    private sealed record SearchResponseBody(
        string? Query,
        string? CorrectedQuery,
        int TotalAvailable,
        List<ItemBody>? Items);

    private sealed record ItemBody(
        string? PropertyId,
        string? ExternalId,
        double? Score,
        string? City,
        string? CityAr,
        string? Area,
        string? AreaAr,
        string? District,
        string? DistrictAr,
        string? Compound,
        string? CompoundAr,
        string? PropertyType,
        string? PropertyTypeAr,
        string? TransactionType,
        string? TransactionTypeAr,
        decimal? Price,
        string? Currency,
        decimal? AreaSqm,
        int? Bedrooms,
        int? Bathrooms,
        string? FinishingStatus,
        bool? Furnished,
        string? View,
        string? Amenities,
        string? ListingStatus,
        string? TitleAr,
        string? TitleEn,
        string? DescriptionAr,
        string? DescriptionEn);
}
