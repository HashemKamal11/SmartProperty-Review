using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Contracts.Marketplace.Public;
using SmartProperty.Api.Contracts.Marketplace.Submitter;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// What the submitter contract can and cannot say, asserted on the types and on their serialized form, and proof
/// that nothing here is reachable from production: no route, no controller and no registration.
/// </summary>
/// <remarks>
/// The response types are a closed vocabulary: every member is a plain value or another response type, so a
/// confidential field cannot be added without failing here and being reviewed. The reachability tests use the real
/// production composition with a dummy connection string; nothing opens a connection.
/// </remarks>
public sealed class SubmitterSubmissionContractPrivacyTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SubmissionId = Guid.Parse("b1d7a3c2-5e44-4a0e-8f21-6c9d2e7a1b10");
    private static readonly Guid ListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Submitted = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Decided = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly Type[] ContractTypes =
    [
        typeof(SubmitterSubmissionSummaryResponse),
        typeof(SubmitterSubmissionDetailResponse),
        typeof(SubmitterSubmissionDecisionResponse),
        typeof(PublicMoneyResponse),
        typeof(PublicLocationResponse),
    ];

    // Name fragments that would indicate confidential or privileged data. Matched case-insensitively against every
    // member name, constructor parameter name and serialized property name. The submitter contract carries no
    // evidence at all, so that word is forbidden outright.
    private static readonly string[] ForbiddenFragments =
    [
        "legalparty",
        "linkeduser",
        "declaredowner",
        "actinguser",
        "submitteruser",
        "userid",
        "owner",
        "reviewer",
        "workspace",
        "propertyid",
        "evidence",
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
            .Concat(JsonKeys(Serialize(PopulatedSummary())))
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
                "CreatedAt", "IsCurrentlyPublished", "ListingId", "Location", "Price", "PropertyType", "Purpose",
                "Representation", "Status", "SubmissionId", "SubmittedAt", "Title", "UpdatedAt", "Version",
            },
            MemberNames(typeof(SubmitterSubmissionSummaryResponse)));
        Assert.Equal(
            new[]
            {
                "CreatedAt", "Decision", "Description", "IsCurrentlyPublished", "ListingId", "Location", "Price",
                "PropertyType", "Purpose", "Representation", "Status", "SubmissionId", "SubmittedAt", "Title",
                "UpdatedAt", "Version",
            },
            MemberNames(typeof(SubmitterSubmissionDetailResponse)));
        Assert.Equal(new[] { "Approved", "DecidedAt", "Reason" }, MemberNames(typeof(SubmitterSubmissionDecisionResponse)));
    }

    [Fact]
    public void TheOnlyIdentifiersAreTheSubmissionAndTheListing()
    {
        Assert.Equal(new[] { "ListingId", "SubmissionId" }, GuidMembers(typeof(SubmitterSubmissionSummaryResponse)));
        Assert.Equal(new[] { "ListingId", "SubmissionId" }, GuidMembers(typeof(SubmitterSubmissionDetailResponse)));

        foreach (var type in new[] { typeof(SubmitterSubmissionDecisionResponse), typeof(PublicMoneyResponse), typeof(PublicLocationResponse) })
        {
            Assert.Empty(GuidMembers(type));
        }
    }

    [Fact]
    public void ASummarySerializesExactlyTheApprovedKeysAndOmitsTheSubmissionTimeOfADraft()
    {
        var full = TopLevelKeys(Serialize(PopulatedSummary()));

        Assert.Equal(
            new[]
            {
                "createdAt", "isCurrentlyPublished", "listingId", "location", "price", "propertyType", "purpose",
                "representation", "status", "submissionId", "submittedAt", "title", "updatedAt", "version",
            },
            full.Order(StringComparer.Ordinal).ToArray());

        var draft = TopLevelKeys(Serialize(SubmitterSubmissionMapper.ToSummaryResponse(DraftSummary())));
        Assert.DoesNotContain("submittedAt", draft);
        Assert.Equal(full.Length - 1, draft.Length);
    }

    [Fact]
    public void ADetailSerializesExactlyTheApprovedKeysAndTheOptionalOnesWhenPresent()
    {
        var full = TopLevelKeys(Serialize(PopulatedDetail()));

        Assert.Equal(
            new[]
            {
                "createdAt", "decision", "description", "isCurrentlyPublished", "listingId", "location", "price",
                "propertyType", "purpose", "representation", "status", "submissionId", "submittedAt", "title",
                "updatedAt", "version",
            },
            full.Order(StringComparer.Ordinal).ToArray());

        var draft = TopLevelKeys(Serialize(SubmitterSubmissionMapper.ToDetailResponse(DraftDetail())));
        Assert.DoesNotContain("submittedAt", draft);
        Assert.DoesNotContain("decision", draft);
        Assert.Equal(full.Length - 2, draft.Length);
    }

    [Fact]
    public void ADecisionSerializesItsReasonOnlyForARejection()
    {
        using var rejected = JsonDocument.Parse(Serialize(PopulatedDetail()));
        Assert.Equal(
            new[] { "approved", "decidedAt", "reason" },
            rejected.RootElement.GetProperty("decision").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var approved = SubmitterSubmissionMapper.ToDetailResponse(
            DraftDetail() with
            {
                Status = ListingSubmissionStatus.Approved,
                SubmittedAt = Submitted,
                UpdatedAt = Decided,
                IsCurrentlyPublished = true,
                Decision = new SubmitterSubmissionDecision(Approved: true, "Never shown.", Decided),
            });
        var json = Serialize(approved);
        using var approvedDocument = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "approved", "decidedAt" },
            approvedDocument.RootElement.GetProperty("decision").EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("Never shown", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePriceCarriesARentPeriodOnlyForARental()
    {
        using var rental = JsonDocument.Parse(Serialize(PopulatedDetail()));
        Assert.Equal(
            new[] { "amount", "currency", "rentPeriod" },
            rental.RootElement.GetProperty("price").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        using var sale = JsonDocument.Parse(Serialize(PopulatedSummary()));
        Assert.Equal(
            new[] { "amount", "currency" },
            sale.RootElement.GetProperty("price").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EnumerationsSerializeAsNamesNeverNumbers()
    {
        using var document = JsonDocument.Parse(Serialize(PopulatedDetail()));
        var root = document.RootElement;

        foreach (var key in new[] { "purpose", "status", "propertyType", "representation" })
        {
            Assert.Equal(JsonValueKind.String, root.GetProperty(key).ValueKind);
        }

        Assert.Equal("Rent", root.GetProperty("purpose").GetString());
        Assert.Equal("Rejected", root.GetProperty("status").GetString());
        Assert.Equal("Monthly", root.GetProperty("price").GetProperty("rentPeriod").GetString());
        Assert.Equal("WorkspaceRepresentative", root.GetProperty("representation").GetString());
    }

    [Fact]
    public void NoSerializedGuidIsAnythingButTheSubmissionAndTheListing()
    {
        var allowed = new[] { SubmissionId, ListingId }.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var json in new[] { Serialize(PopulatedDetail()), Serialize(PopulatedSummary()), Serialize(SubmitterSubmissionMapper.ToDetailResponse(DraftDetail())) })
        {
            var found = Regex.Matches(json, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
                .Select(match => match.Value)
                .ToArray();

            Assert.All(found, value => Assert.Contains(value, allowed));
        }
    }

    [Fact]
    public void NoResponseMemberCanNameWhoseSubmissionsToRead()
    {
        // Identity is the authenticated principal. No response type nor the mapper has a place for a requester.
        foreach (var type in ContractTypes)
        {
            Assert.DoesNotContain(
                PropertiesOf(type).Select(property => property.Name),
                name => name.Contains("user", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("submitter", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("acting", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ----- reachability: nothing here is exposed

    [Fact]
    public void NoControllerActionTakesOrReturnsASubmitterContract()
    {
        var submitterNamespace = typeof(SubmitterSubmissionSummaryResponse).Namespace!;
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

            Assert.DoesNotContain(referenced, type => type.Namespace == submitterNamespace);
        }
    }

    [Fact]
    public void NoSubmitterRouteExists()
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

        Assert.DoesNotContain(routes, route => route.Contains("listing-submissions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("marketplace/me", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("admin/marketplace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheProductionContainerRegistersNeitherTheSubmitterHandlersNorTheirRepository()
    {
        using var root = new SmartPropertyApiFactory(UnusedConnectionString);
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", UnusedConnectionString);
        });
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.Null(provider.GetService<ISubmitterListingSubmissionReadRepository>());
        Assert.Null(provider.GetService<IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>>>());
        Assert.Null(provider.GetService<IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail>>());
    }

    // ----- sample data

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, WebOptions);
    }

    // A pending sale summary: no rent period.
    private static SubmitterSubmissionSummaryResponse PopulatedSummary()
    {
        return SubmitterSubmissionMapper.ToSummaryResponse(PendingSummary());
    }

    // A rejected rental with a reason and a submission time: every optional member.
    private static SubmitterSubmissionDetailResponse PopulatedDetail()
    {
        return SubmitterSubmissionMapper.ToDetailResponse(new SubmitterSubmissionDetail(
            SubmissionId,
            ListingId,
            ListingPurpose.Rent,
            2,
            ListingSubmissionStatus.Rejected,
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicListingPrice(5200m, "SAR", RentPeriod.Monthly),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.WorkspaceRepresentative,
            Created,
            Submitted,
            Decided,
            false,
            new SubmitterSubmissionDecision(Approved: false, "The deed does not cover this parcel.", Decided)));
    }

    private static SubmitterSubmissionSummary PendingSummary()
    {
        return new SubmitterSubmissionSummary(
            SubmissionId,
            ListingId,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.PendingReview,
            "Two bedroom apartment",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            Created,
            Submitted,
            Submitted,
            false);
    }

    private static SubmitterSubmissionSummary DraftSummary()
    {
        return PendingSummary() with
        {
            Status = ListingSubmissionStatus.Draft,
            SubmittedAt = null,
            UpdatedAt = Created.AddHours(1),
        };
    }

    private static SubmitterSubmissionDetail DraftDetail()
    {
        return new SubmitterSubmissionDetail(
            SubmissionId,
            ListingId,
            ListingPurpose.Sale,
            1,
            ListingSubmissionStatus.Draft,
            "Two bedroom apartment",
            "Close to the centre.",
            new PublicListingPrice(1250000m, "SAR", null),
            PropertyType.Unit,
            new PublicListingLocation("SA", "Riyadh Province", "Riyadh", "Al Olaya"),
            ListingRepresentationKind.Owner,
            Created,
            null,
            Created.AddHours(1),
            false,
            null);
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
