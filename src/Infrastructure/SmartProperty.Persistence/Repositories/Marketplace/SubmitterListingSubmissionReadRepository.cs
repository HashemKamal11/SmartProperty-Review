using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>Own-submission reads, scoped in PostgreSQL to the actor supplied by the trusted handlers.</summary>
/// <remarks>
/// Count and page are separate statements at the caller's isolation level. Concurrent commits may move rows
/// relative to the count; every statement still applies the same actor predicate. No locks or writes are taken.
/// </remarks>
internal sealed class SubmitterListingSubmissionReadRepository(ApplicationDbContext context)
    : ISubmitterListingSubmissionReadRepository
{
    private const string SummaryColumns = """
        s.id AS "SubmissionId",
        s.listing_id AS "ListingId",
        s.purpose AS "PurposeText",
        s.version AS "Version",
        s.status AS "StatusText",
        s.content_snapshot->'Payload'->>'Title' AS "Title",
        (s.content_snapshot->'Payload'->>'Amount')::numeric AS "Amount",
        s.content_snapshot->'Payload'->>'Currency' AS "Currency",
        (s.content_snapshot->'Payload'->>'RentPeriod')::integer AS "RentPeriod",
        (s.content_snapshot->'Payload'->>'PropertyType')::integer AS "PropertyType",
        s.content_snapshot->'Payload'->>'Country' AS "Country",
        s.content_snapshot->'Payload'->>'Region' AS "Region",
        s.content_snapshot->'Payload'->>'City' AS "City",
        s.content_snapshot->'Payload'->>'District' AS "District",
        CASE WHEN jsonb_typeof(s.authority_snapshot->'Payload'->'Representation') = 'number'
             THEN (s.authority_snapshot->'Payload'->>'Representation')::integer
             ELSE NULL END AS "Representation",
        s.created_at AS "CreatedAt",
        s.submitted_at AS "SubmittedAt",
        s.updated_at AS "UpdatedAt",
        COALESCE(l.publication_status = 'Published' AND l.published_submission_id = s.id, false)
            AS "IsCurrentlyPublished",
        (s.content_snapshot->'Payload'->'PricingOptions')::text AS "PricingOptionsJson",
        (s.content_snapshot->'Payload'->'RentalTerms')::text AS "RentalTermsJson"
        """;

    private const string ListingJoin = """
        FROM marketplace.submissions AS s
        JOIN marketplace.listings AS l
          ON l.id = s.listing_id AND l.property_id = s.property_id AND l.purpose = s.purpose
        """;

    // The converter writes canonical lower-case D-format GUID strings. Exact text equality is intentional:
    // missing, malformed, differently formatted or empty actor identities cannot become authorization.
    // The actor is used only in WHERE, never SELECT. COLLATE C avoids permissive text collations.
    private const string ActorPredicate = """
        WHERE (s.authority_snapshot->'Payload'->>'ActingUserId') COLLATE "C" = {0}
        """;

    private const string SummarySql = "SELECT " + SummaryColumns + "\n" + ListingJoin + "\n" + ActorPredicate;

    private const string DetailSql = "SELECT " + SummaryColumns + """
        ,
        s.content_snapshot->'Payload'->>'Description' AS "Description",
        d.approved AS "DecisionApproved",
        CASE WHEN s.status = 'Rejected' AND d.approved = false THEN d.reason ELSE NULL END AS "DecisionReason",
        d.decided_at AS "DecidedAt"

        """ + ListingJoin + """

        LEFT JOIN marketplace.review_decisions AS d ON d.submission_id = s.id

        """ + ActorPredicate + " AND s.id = {1}";

    // Only fixed SQL is composed; actor and id values are provider parameters. No full entity is materialized.
    private IQueryable<SubmitterListingSubmissionReadRow> Filter(
        Guid actingUserId, ListingPurpose? purpose, ListingSubmissionStatus? status)
    {
        var query = context.Database.SqlQueryRaw<SubmitterListingSubmissionReadRow>(
            SummarySql, actingUserId.ToString("D"));

        if (purpose is { } selectedPurpose)
        {
            var purposeText = selectedPurpose.ToString();
            query = query.Where(row => row.PurposeText == purposeText);
        }

        if (status is { } selectedStatus)
        {
            var statusText = selectedStatus.ToString();
            query = query.Where(row => row.StatusText == statusText);
        }

        return query;
    }

    public async Task<SubmitterSubmissionPage> ListBySubmitterAsync(
        Guid actingUserId, ListingPurpose? purpose, ListingSubmissionStatus? status,
        int skip, int take, CancellationToken cancellationToken = default)
    {
        RequireActor(actingUserId);
        if (purpose.HasValue && !Enum.IsDefined(purpose.Value))
            throw new ArgumentException("Unsupported listing purpose.", nameof(purpose));
        if (status.HasValue && !Enum.IsDefined(status.Value))
            throw new ArgumentException("Unsupported submission status.", nameof(status));
        if (skip < 0 || skip > PageParameters.MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 1 || take > PageParameters.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(take));
        cancellationToken.ThrowIfCancellationRequested();

        var query = Filter(actingUserId, purpose, status);
        var count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(row => row.UpdatedAt).ThenBy(row => row.SubmissionId)
            .Skip(skip).Take(take).ToListAsync(cancellationToken);
        return new SubmitterSubmissionPage(rows.Select(ToSummary).ToArray(), count);
    }

    public async Task<SubmitterSubmissionDetail?> GetBySubmitterAsync(
        Guid actingUserId, Guid submissionId, CancellationToken cancellationToken = default)
    {
        RequireActor(actingUserId);
        cancellationToken.ThrowIfCancellationRequested();
        if (submissionId == Guid.Empty)
            return null;

        var row = await context.Database.SqlQueryRaw<SubmitterListingSubmissionDetailReadRow>(
                DetailSql, actingUserId.ToString("D"), submissionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var summary = ToSummary(row.Summary());
        SubmitterSubmissionDecision? decision = null;
        if (summary.Status is ListingSubmissionStatus.Draft or ListingSubmissionStatus.PendingReview)
        {
            if (row.DecisionApproved.HasValue || row.DecidedAt.HasValue || row.DecisionReason is not null)
                throw Invalid();
        }
        else
        {
            if (row.DecisionApproved is not { } approved || row.DecidedAt is not { } at
                || at < summary.SubmittedAt || at > summary.UpdatedAt
                || approved != (summary.Status != ListingSubmissionStatus.Rejected))
                throw Invalid();
            decision = new SubmitterSubmissionDecision(approved, approved ? null : Text(row.DecisionReason), at);
        }

        return new SubmitterSubmissionDetail(
            summary.SubmissionId, summary.ListingId, summary.Purpose, summary.Version, summary.Status,
            summary.Title, Text(row.Description), summary.Price, summary.PropertyType, summary.Location,
            summary.Representation, summary.CreatedAt, summary.SubmittedAt, summary.UpdatedAt,
            summary.IsCurrentlyPublished, decision, summary.PricingOptions, summary.RentalTerms);
    }

    private static SubmitterSubmissionSummary ToSummary(SubmitterListingSubmissionReadRow row)
    {
        var purpose = Parse<ListingPurpose>(row.PurposeText);
        var status = Parse<ListingSubmissionStatus>(row.StatusText);
        var propertyType = (PropertyType)row.PropertyType;
        if (!Enum.IsDefined(propertyType) || row.Representation is not { } representation
            || !Enum.IsDefined((ListingRepresentationKind)representation))
            throw Invalid();

        var rentPeriod = (RentPeriod?)row.RentPeriod;
        if ((purpose == ListingPurpose.Sale && rentPeriod.HasValue)
            || (purpose == ListingPurpose.Rent && (rentPeriod is not { } period || !Enum.IsDefined(period))))
            throw Invalid();

        if (row.SubmissionId == Guid.Empty || row.ListingId == Guid.Empty || row.Version < 1
            || row.CreatedAt == default || row.UpdatedAt < row.CreatedAt
            || (status == ListingSubmissionStatus.Draft
                ? row.SubmittedAt.HasValue
                : row.SubmittedAt is not { } submitted || submitted < row.CreatedAt || submitted > row.UpdatedAt)
            || (row.IsCurrentlyPublished && status != ListingSubmissionStatus.Approved))
            throw Invalid();

        return new SubmitterSubmissionSummary(
            row.SubmissionId, row.ListingId, purpose, row.Version, status, Text(row.Title),
            new PublicListingPrice(row.Amount, Text(row.Currency), rentPeriod), propertyType,
            new PublicListingLocation(Text(row.Country), Text(row.Region), Text(row.City), Text(row.District)),
            (ListingRepresentationKind)representation, row.CreatedAt, row.SubmittedAt, row.UpdatedAt,
            row.IsCurrentlyPublished,
            purpose == ListingPurpose.Rent ? PublicListingReadRepository.ParsePricingOptions(row.PricingOptionsJson) : null,
            purpose == ListingPurpose.Rent ? PublicListingReadRepository.ParseRentalTerms(row.RentalTermsJson) : null);
    }

    private static void RequireActor(Guid actingUserId)
    {
        if (actingUserId == Guid.Empty)
            throw new ArgumentException("An authenticated actor is required.", nameof(actingUserId));
    }

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            && parsed.ToString() == value ? parsed : throw Invalid();

    private static string Text(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw Invalid();

    // Never include stored values or identifiers in our diagnostics; provider faults propagate to generic API errors.
    private static InvalidOperationException Invalid() => new("A stored submitter read value is invalid.");
}
