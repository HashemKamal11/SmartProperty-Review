using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SmartProperty.Api.IntegrationTests.Marketplace;

/// <summary>
/// The complete, approved inventory of marketplace routes. Every test that guards the shape of the marketplace surface
/// reads this one list, so adding or removing a route is a deliberate edit here and nowhere else.
/// </summary>
internal static class MarketplaceRouteInventory
{
    /// <summary>Verb and route template, for every route whose path mentions the marketplace.</summary>
    public static readonly string[] Approved =
    [
        // public reads
        "GET api/public/marketplace/listings",
        "GET api/public/marketplace/listings/{listingId:guid}",

        // administrator: review reads, decisions, settings
        "GET api/admin/marketplace/listing-submissions",
        "GET api/admin/marketplace/listing-submissions/{submissionId:guid}",
        "POST api/admin/marketplace/listing-submissions/{submissionId:guid}/approve",
        "POST api/admin/marketplace/listing-submissions/{submissionId:guid}/reject",
        "GET api/admin/marketplace/settings/signing-window",
        "PUT api/admin/marketplace/settings/signing-window",

        // advertiser: drafts, submission, withdrawal
        "GET api/marketplace/me/listing-submissions",
        "GET api/marketplace/me/listing-submissions/{submissionId:guid}",
        "POST api/marketplace/me/listing-submissions",
        "PUT api/marketplace/me/listing-submissions/{submissionId:guid}",
        "DELETE api/marketplace/me/listing-submissions/{submissionId:guid}",
        "POST api/marketplace/me/listing-submissions/{submissionId:guid}/submit",
        "POST api/marketplace/me/properties/{propertyId:guid}/listing-drafts",
        "POST api/marketplace/me/listings/{listingId:guid}/withdraw",

        // customer: requests
        "POST api/marketplace/purchase-requests",
        "POST api/marketplace/rental-requests",
        "GET api/marketplace/me/requests",
        "GET api/marketplace/me/purchase-requests/{requestId:guid}",
        "GET api/marketplace/me/rental-requests/{requestId:guid}",
        "POST api/marketplace/me/purchase-requests/{requestId:guid}/withdraw",
        "POST api/marketplace/me/rental-requests/{requestId:guid}/withdraw",

        // advertiser: received requests
        "GET api/marketplace/owner/requests",
        "GET api/marketplace/owner/purchase-requests/{requestId:guid}",
        "GET api/marketplace/owner/rental-requests/{requestId:guid}",
        "POST api/marketplace/owner/purchase-requests/{requestId:guid}/accept",
        "POST api/marketplace/owner/purchase-requests/{requestId:guid}/reject",
        "POST api/marketplace/owner/rental-requests/{requestId:guid}/accept",
        "POST api/marketplace/owner/rental-requests/{requestId:guid}/reject",

        // commitments
        "GET api/marketplace/me/commitments",
        "GET api/marketplace/me/commitments/{commitmentId:guid}",
        "POST api/marketplace/me/commitments/{commitmentId:guid}/confirm-signed",
        "POST api/marketplace/me/commitments/{commitmentId:guid}/confirm-handover",
        "POST api/marketplace/me/commitments/{commitmentId:guid}/confirm-return",
    ];

    public static readonly string[] ApprovedOrdered = [.. Approved.Order(StringComparer.Ordinal)];

    public static string[] Actual(IServiceProvider services)
    {
        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Where(line => line.Contains("marketplace", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
