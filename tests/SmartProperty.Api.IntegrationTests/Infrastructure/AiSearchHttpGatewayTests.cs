using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.AiSearch;
using SmartProperty.Application.Abstractions.Ai;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Covers the HTTP client of the AI search service against a stub transport, so what is sent and how each answer
/// is read are pinned without ever reaching the real service.
/// </summary>
public sealed class AiSearchHttpGatewayTests
{
    private static readonly AiListing Listing = new(
        12, 7, "Twin House", "Sale", 4_500_000.5m, 180m, 3, 2, 5, "Luxury Finished", false, "City", ["balcony", "pool"],
        "Available", "عنوان", "Title", null, null);

    // ---- adding a listing

    [Fact]
    public async Task APutSendsTheListingInSnakeCaseUnderItsExternalIdWithTheApiKey()
    {
        var stub = new StubHandler(HttpStatusCode.Created, "{}");
        var gateway = CreateGateway(stub, apiKey: "secret-key");

        var result = await gateway.PutListingAsync("0b8c1f8e-1111-2222-3333-444455556666", Listing);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Created);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://ai.test/v1/listings/0b8c1f8e-1111-2222-3333-444455556666", request.Uri);
        Assert.Equal("secret-key", Assert.Single(request.Headers["x-api-key"]));

        using var json = JsonDocument.Parse(request.Body!);
        var root = json.RootElement;
        Assert.Equal(12, root.GetProperty("area_id").GetInt32());
        Assert.Equal(7, root.GetProperty("compound_id").GetInt32());
        Assert.Equal("Twin House", root.GetProperty("property_type").GetString());
        Assert.Equal("Sale", root.GetProperty("transaction_type").GetString());
        Assert.Equal(4_500_000.5m, root.GetProperty("price").GetDecimal());
        Assert.Equal(180m, root.GetProperty("area_sqm").GetDecimal());
        Assert.Equal(3, root.GetProperty("bedrooms").GetInt32());
        Assert.Equal(2, root.GetProperty("bathrooms").GetInt32());
        Assert.Equal(5, root.GetProperty("floor").GetInt32());
        Assert.Equal("Luxury Finished", root.GetProperty("finishing_status").GetString());
        Assert.False(root.GetProperty("furnished").GetBoolean());
        Assert.Equal("City", root.GetProperty("view").GetString());
        Assert.Equal(["balcony", "pool"], root.GetProperty("amenities").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Available", root.GetProperty("listing_status").GetString());
        Assert.Equal("عنوان", root.GetProperty("title_ar").GetString());
        Assert.Equal("Title", root.GetProperty("title_en").GetString());
    }

    [Fact]
    public async Task APutThatReplacedAnExistingListingIsNotReportedAsCreated()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.OK, "{}"));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
    }

    [Fact]
    public async Task OptionalFactsThatAreNullAreLeftOutOfTheBody()
    {
        var stub = new StubHandler(HttpStatusCode.Created, "{}");
        var minimal = Listing with
        {
            CompoundId = null, Floor = null, FinishingStatus = null, Furnished = null, View = null, TitleAr = null
        };

        await CreateGateway(stub).PutListingAsync("abc", minimal);

        using var json = JsonDocument.Parse(stub.Requests[0].Body!);
        var root = json.RootElement;
        Assert.False(root.TryGetProperty("compound_id", out _));
        Assert.False(root.TryGetProperty("floor", out _));
        Assert.False(root.TryGetProperty("finishing_status", out _));
        Assert.False(root.TryGetProperty("furnished", out _));
        Assert.False(root.TryGetProperty("view", out _));
        Assert.False(root.TryGetProperty("title_ar", out _));
        Assert.True(root.TryGetProperty("area_id", out _));
    }

    [Fact]
    public async Task NoApiKeyHeaderIsSentWhenNoneIsConfigured()
    {
        var stub = new StubHandler(HttpStatusCode.Created, "{}");

        await CreateGateway(stub, apiKey: "").PutListingAsync("abc", Listing);

        Assert.False(stub.Requests[0].Headers.ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task AnExternalIdIsEscapedIntoThePath()
    {
        var stub = new StubHandler(HttpStatusCode.Created, "{}");

        await CreateGateway(stub).PutListingAsync("a b/c", Listing);

        Assert.Equal("https://ai.test/v1/listings/a%20b%2Fc", stub.Requests[0].Uri);
    }

    [Fact]
    public async Task A422IsARejectionCarryingTheServicesDetail()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.UnprocessableEntity, "{\"detail\":\"area_id not found\"}"));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.True(result.IsFailure);
        Assert.Equal("ai.rejected", result.Error!.Code);
        Assert.Contains("area_id not found", result.Error.Description);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedKeyIsUnavailableAndPointsAtTheSetting(HttpStatusCode status)
    {
        var gateway = CreateGateway(new StubHandler(status, "{}"));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Contains("AiSearch:ApiKey", result.Error.Description);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AServerErrorIsUnavailable(HttpStatusCode status)
    {
        var gateway = CreateGateway(new StubHandler(status, "boom"));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Contains(((int)status).ToString(), result.Error.Description);
    }

    [Fact]
    public async Task ANetworkFailureIsUnavailableAndNeverThrown()
    {
        var gateway = CreateGateway(new StubHandler(new HttpRequestException("connection refused")));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Contains("could not be reached", result.Error.Description);
    }

    [Fact]
    public async Task ATimeoutIsUnavailable()
    {
        var gateway = CreateGateway(new StubHandler(new TaskCanceledException("timed out")));

        var result = await gateway.PutListingAsync("abc", Listing);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
        Assert.Contains("in time", result.Error.Description);
    }

    [Fact]
    public async Task ACancellationRequestedByTheCallerStillPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var gateway = CreateGateway(new StubHandler(new TaskCanceledException("cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.PutListingAsync("abc", Listing, cancellation.Token));
    }

    [Fact]
    public async Task ADisabledIntegrationSendsNothing()
    {
        var stub = new StubHandler(HttpStatusCode.Created, "{}");
        var gateway = CreateGateway(stub, enabled: false);

        var put = await gateway.PutListingAsync("abc", Listing);
        var delete = await gateway.DeleteListingAsync("abc");
        var search = await gateway.SearchAsync("flat", 3);
        var places = await gateway.GetLocationsAsync("country", null);

        Assert.Equal(AiSearchErrors.DisabledCode, put.Error!.Code);
        Assert.Equal(AiSearchErrors.DisabledCode, delete.Error!.Code);
        Assert.Equal(AiSearchErrors.DisabledCode, search.Error!.Code);
        Assert.Equal(AiSearchErrors.DisabledCode, places.Error!.Code);
        Assert.Empty(stub.Requests);
    }

    // ---- removing a listing

    [Fact]
    public async Task ADeleteTargetsTheListingWithTheApiKey()
    {
        var stub = new StubHandler(HttpStatusCode.OK, "{}");

        var result = await CreateGateway(stub, apiKey: "secret-key").DeleteListingAsync("abc");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("https://ai.test/v1/listings/abc", request.Uri);
        Assert.Equal("secret-key", Assert.Single(request.Headers["x-api-key"]));
    }

    [Fact]
    public async Task DeletingAListingTheServiceNeverHadIsStillASuccess()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.NotFound, "{}"));

        var result = await gateway.DeleteListingAsync("abc");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task AFailedDeleteIsUnavailable()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await gateway.DeleteListingAsync("abc");

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
    }

    // ---- searching

    private const string SearchAnswer = """
        {
          "query": "flat",
          "total_available": 52600,
          "offset": 0,
          "sort": "relevance",
          "corrected_query": "a flat",
          "constraints": { "transaction_type": "Sale" },
          "items": [
            {
              "property_id": "EGY-SALE-APT-007694",
              "listing_id": 57694,
              "city": "Cairo", "city_ar": "القاهرة",
              "area": "Shorouk City", "area_ar": "مدينة الشروق",
              "district": "Third District", "district_ar": "الحي الثالث",
              "compound": null, "compound_ar": null,
              "developer": "Local Property Management",
              "property_type": "Apartment", "property_type_ar": "شقة",
              "transaction_type": "Sale", "transaction_type_ar": "بيع",
              "price": 4320000.0, "currency": "EGP", "area_sqm": 196.0,
              "bedrooms": 3, "bathrooms": 3,
              "finishing_status": "Finished", "furnished": false, "view": null,
              "amenities": "Security | Parking | Elevator",
              "listing_status": "Available",
              "published_at": null, "age_days": null,
              "price_per_sqm": 22040.82, "market_median_per_sqm": 26475.805, "market_sample": 452,
              "price_vs_market": -0.1675, "price_percentile": 27,
              "title_ar": "شقة للبيع", "title_en": "Apartment for sale",
              "description_ar": "شقة غير مفروشة", "description_en": "Unfurnished apartment",
              "score": 0.21640871
            },
            { "property_id": "", "score": 0.1 },
            { "property_id": "PF-QEY1G136CVS70QHDZQECX67P48", "external_id": "0b8c1f8e-1111-2222-3333-444455556666", "score": null, "price": "125000.5" }
          ]
        }
        """;

    [Fact]
    public async Task ASearchPostsTheQueryAndTopKAndReadsTheRankedItems()
    {
        var stub = new StubHandler(HttpStatusCode.OK, SearchAnswer);

        var result = await CreateGateway(stub).SearchAsync("flat", 3);

        Assert.True(result.IsSuccess);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ai.test/v1/search", request.Uri);
        Assert.False(request.Headers.ContainsKey("x-api-key"));
        using (var json = JsonDocument.Parse(request.Body!))
        {
            Assert.Equal("flat", json.RootElement.GetProperty("query").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("top_k").GetInt32());
        }

        var outcome = result.Value;
        Assert.Equal("flat", outcome.Query);
        Assert.Equal("a flat", outcome.CorrectedQuery);
        Assert.Equal(52600, outcome.TotalAvailable);

        // The item without an id is dropped; the other two are kept in order.
        Assert.Equal(2, outcome.Items.Count);
        var first = outcome.Items[0];
        Assert.Equal("EGY-SALE-APT-007694", first.PropertyId);
        Assert.Null(first.ExternalId);
        Assert.Equal(0.21640871, first.Score);
        Assert.Equal("Cairo", first.City);
        Assert.Equal("القاهرة", first.CityAr);
        Assert.Equal("Shorouk City", first.Area);
        Assert.Equal("Apartment", first.PropertyType);
        Assert.Equal("شقة", first.PropertyTypeAr);
        Assert.Equal("Sale", first.TransactionType);
        Assert.Equal(4320000m, first.Price);
        Assert.Equal("EGP", first.Currency);
        Assert.Equal(196m, first.AreaSqm);
        Assert.Equal(3, first.Bedrooms);
        Assert.Equal("Finished", first.FinishingStatus);
        Assert.False(first.Furnished);
        Assert.Null(first.View);
        Assert.Equal("Security | Parking | Elevator", first.Amenities);
        Assert.Equal("شقة غير مفروشة", first.DescriptionAr);
        Assert.Equal("Unfurnished apartment", first.DescriptionEn);

        var second = outcome.Items[1];
        Assert.Equal("PF-QEY1G136CVS70QHDZQECX67P48", second.PropertyId);
        Assert.Equal("0b8c1f8e-1111-2222-3333-444455556666", second.ExternalId);
        Assert.Null(second.Score);
        Assert.Equal(125000.5m, second.Price);
    }

    [Fact]
    public async Task ASearchTheServiceRejectsIsARejection()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.UnprocessableEntity, "{\"detail\":\"query too long\"}"));

        var result = await gateway.SearchAsync("flat", 3);

        Assert.Equal("ai.rejected", result.Error!.Code);
        Assert.Contains("query too long", result.Error.Description);
    }

    [Fact]
    public async Task ASearchTheServiceFailsOnIsUnavailable()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.BadGateway, "bad gateway"));

        var result = await gateway.SearchAsync("flat", 3);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task AnUnreadableSearchAnswerIsUnavailableRatherThanAnException(string body)
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.OK, body));

        var result = await gateway.SearchAsync("flat", 3);

        Assert.True(result.IsFailure);
        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
    }

    [Fact]
    public async Task ASearchAnswerWithNoItemsIsAnEmptyOutcomeThatKeepsTheQuery()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.OK, "{\"total_available\": 0}"));

        var result = await gateway.SearchAsync("flat", 3);

        Assert.True(result.IsSuccess);
        Assert.Equal("flat", result.Value.Query);
        Assert.Equal("flat", result.Value.CorrectedQuery);
        Assert.Empty(result.Value.Items);
    }

    // ---- locations

    [Fact]
    public async Task ACountryListIsRequestedWithoutAParent()
    {
        var stub = new StubHandler(
            HttpStatusCode.OK,
            "[{\"id\":1,\"name_en\":\"Egypt\",\"name_ar\":\"مصر\"},{\"id\":3,\"name_en\":\"UAE\",\"name_ar\":\"الإمارات\"}]");

        var result = await CreateGateway(stub).GetLocationsAsync("country", null);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://ai.test/v1/locations?level=country", stub.Requests[0].Uri);
        Assert.Equal(HttpMethod.Get, stub.Requests[0].Method);
        Assert.Equal([new AiPlace(1, "Egypt", "مصر"), new AiPlace(3, "UAE", "الإمارات")], result.Value);
    }

    [Fact]
    public async Task ALowerLevelIsRequestedUnderItsParent()
    {
        var stub = new StubHandler(HttpStatusCode.OK, "[]");

        var result = await CreateGateway(stub).GetLocationsAsync("area", 14);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Equal("https://ai.test/v1/locations?level=area&parent_id=14", stub.Requests[0].Uri);
    }

    [Fact]
    public async Task AFailedLocationCallIsUnavailable()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await gateway.GetLocationsAsync("country", null);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
    }

    [Fact]
    public async Task AnUnreadableLocationAnswerIsUnavailable()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.OK, "{\"not\":\"a list\"}"));

        var result = await gateway.GetLocationsAsync("country", null);

        Assert.Equal(AiSearchErrors.UnavailableCode, result.Error!.Code);
    }

    // ---- options

    [Theory]
    [InlineData(false, "", 0, true)]
    [InlineData(true, "https://ai-search.neurix.uk", 30, true)]
    [InlineData(true, "http://localhost:8000", 1, true)]
    [InlineData(true, "https://ai-search.neurix.uk", 300, true)]
    [InlineData(true, "", 30, false)]
    [InlineData(true, "ai-search.neurix.uk", 30, false)]
    [InlineData(true, "ftp://ai-search.neurix.uk", 30, false)]
    [InlineData(true, "https://ai-search.neurix.uk", 0, false)]
    [InlineData(true, "https://ai-search.neurix.uk", 301, false)]
    public void TheOptionsAreValidOnlyWhenOffOrFullySpecified(bool enabled, string baseUrl, int timeout, bool expected)
    {
        var options = new AiSearchOptions { Enabled = enabled, BaseUrl = baseUrl, TimeoutSeconds = timeout };

        Assert.Equal(expected, options.IsValid());
    }

    [Fact]
    public void TheOptionsAreOffByDefaultAndPointAtTheKnownService()
    {
        var options = new AiSearchOptions();

        Assert.False(options.Enabled);
        Assert.Equal("https://ai-search.neurix.uk", options.BaseUrl);
        Assert.Equal(string.Empty, options.ApiKey);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.True(options.IsValid());
    }

    private static AiSearchHttpGateway CreateGateway(StubHandler handler, string apiKey = "", bool enabled = true)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ai.test/") };

        return new AiSearchHttpGateway(
            client,
            Options.Create(new AiSearchOptions { Enabled = enabled, BaseUrl = "https://ai.test", ApiKey = apiKey }),
            NullLogger<AiSearchHttpGateway>.Instance);
    }

    /// <summary>A transport that records each request and answers with one scripted response or exception.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _exception;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception exception)
        {
            _exception = exception;
            _body = string.Empty;
        }

        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                body));

            cancellationToken.ThrowIfCancellationRequested();

            if (_exception is not null)
            {
                throw _exception;
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record Recorded(
        HttpMethod Method,
        string Uri,
        Dictionary<string, string[]> Headers,
        string? Body);
}
