using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Contracts.Marketplace.Admin;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// What the Platform Admin review contract can and cannot say, asserted on the types and on their serialized form,
/// and proof that nothing here is reachable from production: no route, no controller and no registration.
/// </summary>
/// <remarks>
/// The response types are a closed vocabulary: every member is a plain value or another response type, so a
/// confidential field cannot be added without failing here and being reviewed. The reachability tests use the real
/// production composition with a dummy connection string; nothing opens a connection.
/// </remarks>
public sealed class AdminReviewContractPrivacyTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SubmissionId = Guid.Parse("b1d7a3c2-5e44-4a0e-8f21-6c9d2e7a1b10");
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly Guid PropertyId = Guid.Parse("c7f2d9e4-1a6b-4c3d-8e5f-0b9a8d7c6e50");
    private static readonly Guid PredecessorId = Guid.Parse("d3e8b1a7-9c2f-4d6e-a5b4-7f1e0c2d3a90");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly Type[] ContractTypes =
    [
        typeof(ReviewQueueItemResponse),
        typeof(ReviewSubmissionDetailResponse),
        typeof(ReviewDecisionResponse),
        typeof(PublicMoneyResponse),
        typeof(PublicLocationResponse),
    ];

    // Name fragments that would indicate confidential or privileged data. Matched case-insensitively against every
    // member name, constructor parameter name and serialized property name. Evidence is handled separately because
    // the count and the aggregate flag are allowed and nothing else about evidence is.
    private static readonly string[] ForbiddenFragments =
    [
        "legalparty",
        "linkeduser",
        "declaredowner",
        "actinguser",
        "submitteruser",
        "userid",
        "reviewer",
        "workspace",
        "sha",
        "digest",
        "hash",
        "assessment",
        "snapshot",
        "verif",
        "scan",
        "independ",
        "conflict",
        "affiliat",
        "concurrency",
        "storage",
        "email",
        "phone",
        "token",
        "secret",
        "password",
        "uri",
        "url",
        "raw",
        "json",
    ];

    [Fact]
    public void NoMemberParameterOrJsonKeyNameSuggestsConfidentialData()
    {
        var names = ContractTypes
            .SelectMany(type => PropertiesOf(type).Select(property => property.Name)
                .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.Name!)))
            .Concat(JsonKeys(Serialize(PopulatedDetail())))
            .Concat(JsonKeys(Serialize(PopulatedQueueItem())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var name in names)
        {
            foreach (var fragment in ForbiddenFragments)
            {
                Assert.False(
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"'{name}' contains the forbidden fragment '{fragment}'.");
            }
        }
    }

    [Fact]
    public void EvidenceIsOnlyEverACountAndOneAggregateFlag()
    {
        var evidenceMembers = ContractTypes
            .SelectMany(type => PropertiesOf(type).Select(property => $"{type.Name}.{property.Name}:{property.PropertyType.Name}"))
            .Where(text => text.Contains("evidence", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ReviewQueueItemResponse.AllEvidenceClean:Boolean",
                "ReviewQueueItemResponse.EvidenceCount:Int32",
                "ReviewSubmissionDetailResponse.AllEvidenceClean:Boolean",
                "ReviewSubmissionDetailResponse.EvidenceCount:Int32",
            },
            evidenceMembers);
    }

    [Fact]
    public void EveryMemberIsAPlainValueOrAnotherResponseType()
    {
        foreach (var type in ContractTypes)
        {
            foreach (var property in PropertiesOf(type))
            {
                Assert.True(
                    IsAllowedMemberType(property.PropertyType),
                    $"{type.Name}.{property.Name} has type {property.PropertyType}, which is not an allowed response type.");
            }
        }
    }

    [Fact]
    public void EachResponseHasExactlyTheApprovedMembers()
    {
        Assert.Equal(
            new[]
            {
                "AllEvidenceClean", "EvidenceCount", "ListingId", "Location", "OwnerKind", "Price", "PropertyType",
                "Purpose", "Representation", "Status", "SubmissionId", "SubmittedAt", "Title", "Version",
            },
            MemberNames(typeof(ReviewQueueItemResponse)));
        Assert.Equal(
            new[]
            {
                "AllEvidenceClean", "AuthorityValidFrom", "AuthorityValidUntil", "CreatedAt", "Decision", "Description",
                "EvidenceCount", "IsCurrentlyPublished", "ListingId", "Location", "OwnerKind", "PredecessorSubmissionId",
                "Price", "PropertyId", "PropertyType", "Purpose", "Representation", "Status", "SubmissionId",
                "SubmittedAt", "Title", "UpdatedAt", "Version",
            },
            MemberNames(typeof(ReviewSubmissionDetailResponse)));
        Assert.Equal(new[] { "Approved", "DecidedAt", "Reason" }, MemberNames(typeof(ReviewDecisionResponse)));
    }

    [Fact]
    public void TheOnlyIdentifiersAreTheSubmissionTheListingThePropertyAndThePredecessor()
    {
        Assert.Equal(new[] { "ListingId", "SubmissionId" }, GuidMembers(typeof(ReviewQueueItemResponse)));
        Assert.Equal(
            new[] { "ListingId", "PredecessorSubmissionId", "PropertyId", "SubmissionId" },
            GuidMembers(typeof(ReviewSubmissionDetailResponse)));

        foreach (var type in new[] { typeof(ReviewDecisionResponse), typeof(PublicMoneyResponse), typeof(PublicLocationResponse) })
        {
            Assert.Empty(GuidMembers(type));
        }
    }

    [Fact]
    public void AQueueItemSerializesExactlyTheApprovedKeys()
    {
        var keys = TopLevelKeys(Serialize(PopulatedQueueItem()));

        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "evidenceCount", "listingId", "location", "ownerKind", "price", "propertyType",
                "purpose", "representation", "status", "submissionId", "submittedAt", "title", "version",
            },
            keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ADetailSerializesExactlyTheApprovedKeysAndTheOptionalOnesWhenPresent()
    {
        var full = TopLevelKeys(Serialize(PopulatedDetail()));

        Assert.Equal(
            new[]
            {
                "allEvidenceClean", "authorityValidFrom", "authorityValidUntil", "createdAt", "decision", "description",
                "evidenceCount", "isCurrentlyPublished", "listingId", "location", "ownerKind", "predecessorSubmissionId",
                "price", "propertyId", "propertyType", "purpose", "representation", "status", "submissionId",
                "submittedAt", "title", "updatedAt", "version",
            },
            full.Order(StringComparer.Ordinal).ToArray());

        var minimal = TopLevelKeys(Serialize(MinimalDetail()));
        Assert.DoesNotContain("authorityValidUntil", minimal);
        Assert.DoesNotContain("predecessorSubmissionId", minimal);
        Assert.DoesNotContain("decision", minimal);
        Assert.Equal(full.Length - 3, minimal.Length);
    }

    [Fact]
    public void ADecisionSerializesItsReasonOnlyForARejection()
    {
        using var rejected = JsonDocument.Parse(Serialize(PopulatedDetail()));
        var rejectedDecision = rejected.RootElement.GetProperty("decision");
        Assert.Equal(
            new[] { "approved", "decidedAt", "reason" },
            rejectedDecision.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var approved = AdminReviewMapper.ToDetailResponse(ApprovedReadModel());
        using var approvedDocument = JsonDocument.Parse(Serialize(approved));
        Assert.Equal(
            new[] { "approved", "decidedAt" },
            approvedDocument.RootElement.GetProperty("decision").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ThePriceCarriesARentPeriodOnlyForARental()
    {
        using var rental = JsonDocument.Parse(Serialize(PopulatedDetail()));
        Assert.Equal(
            new[] { "amount", "currency", "rentPeriod" },
            rental.RootElement.GetProperty("price").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        using var sale = JsonDocument.Parse(Serialize(MinimalDetail()));
        Assert.Equal(
            new[] { "amount", "currency" },
            sale.RootElement.GetProperty("price").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EnumerationsSerializeAsNamesNeverNumbers()
    {
        using var document = JsonDocument.Parse(Serialize(PopulatedDetail()));
        var root = document.RootElement;

        foreach (var key in new[] { "purpose", "status", "propertyType", "representation", "ownerKind" })
        {
            Assert.Equal(JsonValueKind.String, root.GetProperty(key).ValueKind);
        }

        Assert.Equal("Rent", root.GetProperty("purpose").GetString());
        Assert.Equal("Rejected", root.GetProperty("status").GetString());
        Assert.Equal("Monthly", root.GetProperty("price").GetProperty("rentPeriod").GetString());
        Assert.Equal("WorkspaceRepresentative", root.GetProperty("representation").GetString());
        Assert.Equal("Organization", root.GetProperty("ownerKind").GetString());
    }

    [Fact]
    public void NoSerializedGuidIsAnythingButTheFourApprovedIdentifiers()
    {
        var allowed = new[] { SubmissionId, ListingId, PropertyId, PredecessorId }.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var json in new[] { Serialize(PopulatedDetail()), Serialize(MinimalDetail()), Serialize(PopulatedQueueItem()) })
        {
            var found = Regex.Matches(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
                .Select(match => match.Value)
                .ToArray();

            Assert.All(found, value => Assert.Contains(value, allowed));
        }
    }

    // ----- reachability: nothing here is exposed

    [Fact]
    public void NoControllerActionTakesOrReturnsAnAdminReviewContract()
    {
        var adminNamespace = typeof(ReviewQueueItemResponse).Namespace!;
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .ToArray();

        Assert.NotEmpty(controllers);

        foreach (var controller in controllers)
        {
            var referenced = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
                .SelectMany(Flatten)
                .ToArray();

            Assert.DoesNotContain(referenced, type => type.Namespace == adminNamespace);
        }
    }

    [Fact]
    public void NoRouteExistsUnderTheAdminMarketplacePath()
    {
        using var root = new SmartPropertyApiFactory(UnusedConnectionString);
        using var host = root.WithWebHostBuilder(builder =>
        {
            // AddPersistence reads the connection string while Program is composed, so it is supplied as a host
            // setting. Nothing opens a connection with it.
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);
        });
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        // The inventory is real: the existing administration and public routes are visible to it.
        Assert.Contains(routes, route => route.StartsWith("api/admin/workspace-access-requests", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(routes, route => route.StartsWith("api/public/marketplace/listings", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(routes, route => route.Contains("admin/marketplace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("listing-submissions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheProductionContainerRegistersNeitherTheReviewHandlersNorTheAuthorizer()
    {
        using var root = new SmartPropertyApiFactory(UnusedConnectionString);
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);
        });
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.Null(provider.GetService<IReviewReadAuthorizer>());
        Assert.Null(provider.GetService<IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>>());
        Assert.Null(provider.GetService<IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>>());
    }

    // ----- sample data

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, WebOptions);
    }

    private static ReviewQueueItemResponse PopulatedQueueItem()
    {
        return AdminReviewMapper.ToQueueItemResponse(new ReviewQueueItem(
            SubmissionId,
            ListingId,
            ListingPurpose.Rent,
            2,
            ListingSubmissionStatus.PendingReview,
            "Bright apartment",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.WorkspaceRepresentative,
            ListingOwnerKind.Organization,
            Submitted,
            2,
            true));
    }

    // A rejected rental, second version, with an authority window, a predecessor and a reason: every optional member.
    private static ReviewSubmissionDetailResponse PopulatedDetail()
    {
        return AdminReviewMapper.ToDetailResponse(new ReviewSubmissionDetail(
            SubmissionId,
            ListingId,
            PropertyId,
            PredecessorId,
            ListingPurpose.Rent,
            2,
            ListingSubmissionStatus.Rejected,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.WorkspaceRepresentative,
            ListingOwnerKind.Organization,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Created,
            Submitted,
            Decided,
            2,
            true,
            false,
            new ReviewDecisionSummary(Approved: false, "The deed does not cover this parcel.", Decided)));
    }

    // A pending sale, first version: none of the optional members.
    private static ReviewSubmissionDetailResponse MinimalDetail()
    {
        return AdminReviewMapper.ToDetailResponse(new ReviewSubmissionDetail(
            SubmissionId,
            ListingId,
            PropertyId,
            null,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.PendingReview,
            "Two bedroom apartment",
            "Close to the centre.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            ListingOwnerKind.Individual,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            Created,
            Submitted,
            Submitted,
            2,
            true,
            false,
            null));
    }

    private static ReviewSubmissionDetail ApprovedReadModel()
    {
        return new ReviewSubmissionDetail(
            SubmissionId,
            ListingId,
            PropertyId,
            null,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.Approved,
            "Two bedroom apartment",
            "Close to the centre.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            ListingOwnerKind.Individual,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            null,
            Created,
            Submitted,
            Decided,
            2,
            true,
            true,
            new ReviewDecisionSummary(Approved: true, "Never shown.", Decided));
    }

    // ----- helpers

    private static string[] TopLevelKeys(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static string[] JsonKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        var keys = new List<string>();
        Collect(document.RootElement, keys);

        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void Collect(JsonElement element, List<string> keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            keys.Add(property.Name);
            Collect(property.Value, keys);
        }
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name != "EqualityContract")
            .ToArray();
    }

    private static string[] MemberNames(Type type)
    {
        return PropertiesOf(type).Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] GuidMembers(Type type)
    {
        return PropertiesOf(type)
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsAllowedMemberType(Type memberType)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;

        return type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(decimal)
            || type == typeof(int)
            || type == typeof(bool)
            || type == typeof(DateTimeOffset)
            || ContractTypes.Contains(type);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }

        if (type.IsArray)
        {
            foreach (var element in Flatten(type.GetElementType()!))
            {
                yield return element;
            }
        }
    }
}
