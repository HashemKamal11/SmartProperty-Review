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

        // staff: advertising authority (legal review, issuance, revocation)
        "GET api/admin/marketplace/authority-requests",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}/documents/{documentId:guid}/file",
        "POST api/admin/marketplace/authority-requests/{requestId:guid}/legal-approve",
        "POST api/admin/marketplace/authority-requests/{requestId:guid}/issue",
        "POST api/admin/marketplace/authority-requests/{requestId:guid}/reject",
        "POST api/admin/marketplace/authority-requests/{requestId:guid}/request-completion",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}/property",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}/documents",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}/documents/{documentId:guid}",
        "GET api/admin/marketplace/authority-requests/{requestId:guid}/documents/{documentId:guid}/versions/{versionId:guid}/file",
        "GET api/admin/marketplace/authority-grants",
        "POST api/admin/marketplace/authority-grants/{grantId:guid}/revoke",

        // owner: asking for advertising authority
        "GET api/marketplace/authority-document-types",
        "POST api/marketplace/me/authority-requests",
        "GET api/marketplace/me/authority-requests",
        "GET api/marketplace/me/authority-requests/{requestId:guid}",
        "PUT api/marketplace/me/authority-requests/{requestId:guid}",
        "PUT api/marketplace/me/authority-requests/{requestId:guid}/listings/{purpose}",
        "POST api/marketplace/me/authority-requests/{requestId:guid}/documents",
        "POST api/marketplace/me/authority-requests/{requestId:guid}/documents/batch",
        "DELETE api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}",
        "GET api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}/file",
        "GET api/marketplace/me/authority-requests/{requestId:guid}/documents",
        "GET api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}",
        "PUT api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}",
        "POST api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}/rescan",
        "GET api/marketplace/me/authority-requests/{requestId:guid}/documents/{documentId:guid}/versions/{versionId:guid}/file",
        "POST api/marketplace/me/authority-requests/{requestId:guid}/submit",
        "POST api/marketplace/me/authority-requests/{requestId:guid}/withdraw",
        "GET api/marketplace/me/authority-grants",

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
