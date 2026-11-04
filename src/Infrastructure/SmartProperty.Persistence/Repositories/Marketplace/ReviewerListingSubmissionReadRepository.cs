using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>
/// The administrative review reads, answered from persisted submissions only.
/// </summary>
/// <remarks>
/// <para>
/// <b>No identity.</b> Reading does not depend on who is reading or on reviewer independence, so no query here takes
/// or filters by a user. Permission to read is decided before this repository is called.
/// </para>
/// <para>
/// <b>Redaction by construction.</b> The SQL selects an allowlist of columns and snapshot members. It never selects
/// <c>review_decisions.reason</c>, the reviewer or assessment snapshots, the legal-party reference, the acting or
/// linked user, the Workspace, or any evidence identifier or digest; the evidence digests are compared inside the
/// database to produce one boolean and are not returned. A Draft is excluded by the base query itself.
/// </para>
/// <para>
/// <b>AllEvidenceClean</b> is a historical, submit-time summary. It is true only when the submission has at least one
/// evidence reference, the references are distinct, there is exactly one submit-time assessment per reference and no
/// other, and each reference has exactly one assessment with the same evidence id, version id, property id, purpose
/// and digest whose status is Clean. Anything missing, mismatched, pending, failed, malformed or ambiguous is false.
/// It is not proof of legal ownership, current malware safety or current authority.
/// </para>
/// <para>
/// Count and page are two statements, not a repeatable-read snapshot: a concurrent commit may change which rows a
/// page holds relative to the count.
/// </para>
/// </remarks>
internal sealed class ReviewerListingSubmissionReadRepository(ApplicationDbContext context) : IReviewerListingSubmissionReadRepository
{
    // Fixed composable SQL; all caller values enter through parameterized LINQ predicates. Value-converted Domain
    // snapshots cannot safely be traversed through ordinary EF LINQ, so the approved members are projected here.
    // JSON array safety is structural: the LATERAL "j" normalizes each snapshot payload once, through CASE, to the payload
    // itself when it is a jsonb array and to '[]' otherwise (missing key, SQL or JSON null, object, scalar). Every
    // jsonb_array_length / jsonb_array_elements call below takes only j.evidence or j.assessments, so no array function
    // can ever receive a non-array value regardless of how the planner orders the surrounding predicates. A malformed
    // payload therefore yields EvidenceCount 0 and AllEvidenceClean false rather than an error.
    //
    // Scalar casts are deliberately fail-closed. The content casts (Amount, RentPeriod, PropertyType) are guaranteed
    // castable by ck_marketplace_submissions_content. The authority casts (Representation, OwnerKind, ValidFrom,
    // ValidUntil) are not constrained by the schema: a malformed value makes PostgreSQL raise a data exception, the
    // read fails, and the API answers with its generic 500 that discloses nothing. A corrupt authority snapshot is never
    // shown with guessed or missing values.
    private const string ReviewableSql = """
        SELECT s.id AS "SubmissionId",
               s.listing_id AS "ListingId",
               s.property_id AS "PropertyId",
               s.predecessor_id AS "PredecessorSubmissionId",
               s.purpose AS "PurposeText",
               s.version AS "Version",
               s.status AS "StatusText",
               s.content_snapshot->'Payload'->>'Title' AS "Title",
               s.content_snapshot->'Payload'->>'Description' AS "Description",
               (s.content_snapshot->'Payload'->>'Amount')::numeric AS "Amount",
               s.content_snapshot->'Payload'->>'Currency' AS "Currency",
               (s.content_snapshot->'Payload'->>'RentPeriod')::integer AS "RentPeriod",
               (s.content_snapshot->'Payload'->>'PropertyType')::integer AS "PropertyType",
               s.content_snapshot->'Payload'->>'Country' AS "Country",
               s.content_snapshot->'Payload'->>'Region' AS "Region",
               s.content_snapshot->'Payload'->>'City' AS "City",
               s.content_snapshot->'Payload'->>'District' AS "District",
               (s.authority_snapshot->'Payload'->>'Representation')::integer AS "Representation",
               (s.authority_snapshot->'Payload'->'DeclaredOwner'->>'Kind')::integer AS "OwnerKind",
               (s.authority_snapshot->'Payload'->>'ValidFrom')::timestamptz AS "AuthorityValidFrom",
               (s.authority_snapshot->'Payload'->>'ValidUntil')::timestamptz AS "AuthorityValidUntil",
               s.created_at AS "CreatedAt",
               s.submitted_at AS "SubmittedAt",
               s.updated_at AS "UpdatedAt",
               jsonb_array_length(j.evidence) AS "EvidenceCount",
               COALESCE((
                 jsonb_array_length(j.evidence) > 0
                 AND jsonb_array_length(j.evidence) = jsonb_array_length(j.assessments)
                 AND jsonb_array_length(j.evidence) = (
                       SELECT count(DISTINCT (e.value->>'EvidenceId', e.value->>'VersionId'))
                       FROM jsonb_array_elements(j.evidence) AS e(value)
                       WHERE jsonb_typeof(e.value) = 'object')
                 AND jsonb_array_length(j.evidence) = (
                       SELECT count(*)
                       FROM jsonb_array_elements(j.evidence) AS e(value)
                       WHERE jsonb_typeof(e.value) = 'object'
                         AND (SELECT count(*)
                              FROM jsonb_array_elements(j.assessments) AS a(value)
                              WHERE jsonb_typeof(a.value) = 'object' AND jsonb_typeof(a.value->'Evidence') = 'object'
                                AND a.value->'Evidence'->>'EvidenceId' = e.value->>'EvidenceId'
                                AND a.value->'Evidence'->>'VersionId' = e.value->>'VersionId') = 1
                         AND EXISTS (SELECT 1
                                     FROM jsonb_array_elements(j.assessments) AS a(value)
                                     WHERE jsonb_typeof(a.value) = 'object' AND jsonb_typeof(a.value->'Evidence') = 'object'
                                       AND a.value->'Evidence'->>'EvidenceId' = e.value->>'EvidenceId'
                                       AND a.value->'Evidence'->>'VersionId' = e.value->>'VersionId'
                                       AND a.value->'Evidence'->>'PropertyId' = e.value->>'PropertyId'
                                       AND a.value->'Evidence'->>'Purpose' = e.value->>'Purpose'
                                       AND a.value->'Evidence'->>'Sha256' = e.value->>'Sha256'
                                       AND e.value->>'Sha256' IS NOT NULL
                                       AND jsonb_typeof(a.value->'Status') = 'number'
                                       AND a.value->>'Status' = '2'))
               ), false) AS "AllEvidenceClean",
               COALESCE(l.published_submission_id = s.id, false) AS "IsCurrentlyPublished",
               d.approved AS "DecisionApproved",
               d.decided_at AS "DecidedAt"
        FROM marketplace.submissions AS s
        CROSS JOIN LATERAL (
            SELECT CASE WHEN jsonb_typeof(s.evidence_snapshot->'Payload') = 'array'
                        THEN s.evidence_snapshot->'Payload' ELSE '[]'::jsonb END AS evidence,
                   CASE WHEN jsonb_typeof(s.submitted_assessments_snapshot->'Payload') = 'array'
                        THEN s.submitted_assessments_snapshot->'Payload' ELSE '[]'::jsonb END AS assessments
        ) AS j
        JOIN marketplace.listings AS l
          ON l.id = s.listing_id AND l.property_id = s.property_id AND l.purpose = s.purpose
        LEFT JOIN marketplace.review_decisions AS d ON d.submission_id = s.id
        WHERE s.status <> 'Draft'
        """;

    private IQueryable<ReviewerListingSubmissionReadRow> Reviewable() =>
        context.Database.SqlQueryRaw<ReviewerListingSubmissionReadRow>(ReviewableSql);

    public async Task<ReviewQueuePage> ListForReviewAsync(
        ListingPurpose? purpose,
        ListingSubmissionStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(status) || status == ListingSubmissionStatus.Draft)
        {
            throw new ArgumentException("A review queue cannot be requested for this status.", nameof(status));
        }

        if (purpose.HasValue && !Enum.IsDefined(purpose.Value))
        {
            throw new ArgumentException("Unsupported listing purpose.", nameof(purpose));
        }

        if (skip < 0 || skip > PageParameters.MaxOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(skip));
        }

        if (take < 1 || take > PageParameters.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        // Status and purpose narrow the set before it is counted or paged.
        var statusText = status.ToString();
        var query = Reviewable().Where(x => x.StatusText == statusText);

        if (purpose is { } selectedPurpose)
        {
            var purposeText = selectedPurpose.ToString();
            query = query.Where(x => x.PurposeText == purposeText);
        }

        var count = await query.LongCountAsync(cancellationToken);

        var ordered = status == ListingSubmissionStatus.PendingReview
            ? query.OrderBy(x => x.SubmittedAt).ThenBy(x => x.SubmissionId)
            : query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.SubmissionId);

        var rows = await ordered.Skip(skip).Take(take).ToListAsync(cancellationToken);

        return new ReviewQueuePage(rows.Select(ToQueueItem).ToList(), count);
    }

    public async Task<ReviewDetailLookup> GetForReviewAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty)
        {
            return ReviewDetailLookup.NotFound();
        }

        var row = await Reviewable().Where(x => x.SubmissionId == submissionId).SingleOrDefaultAsync(cancellationToken);

        return row is null ? ReviewDetailLookup.NotFound() : ReviewDetailLookup.Found(ToDetail(row));
    }

    private static ReviewQueueItem ToQueueItem(ReviewerListingSubmissionReadRow row)
    {
        return new ReviewQueueItem(
            row.SubmissionId,
            row.ListingId,
            Parse<ListingPurpose>(row.PurposeText),
            row.Version,
            Parse<ListingSubmissionStatus>(row.StatusText),
            row.Title,
            Price(row),
            (PropertyType)row.PropertyType,
            Location(row),
            (ListingRepresentationKind)row.Representation,
            (ListingOwnerKind)row.OwnerKind,
            row.SubmittedAt,
            row.EvidenceCount,
            row.AllEvidenceClean);
    }

    private static ReviewSubmissionDetail ToDetail(ReviewerListingSubmissionReadRow row)
    {
        // The reason is never selected, so a decision summary can only carry the outcome and the time.
        var decision = row.DecisionApproved is { } approved && row.DecidedAt is { } decidedAt
            ? new ReviewDecisionSummary(approved, null, decidedAt)
            : null;

        return new ReviewSubmissionDetail(
            row.SubmissionId,
            row.ListingId,
            row.PropertyId,
            row.PredecessorSubmissionId,
            Parse<ListingPurpose>(row.PurposeText),
            row.Version,
            Parse<ListingSubmissionStatus>(row.StatusText),
            row.Title,
            row.Description,
            Price(row),
            (PropertyType)row.PropertyType,
            Location(row),
            (ListingRepresentationKind)row.Representation,
            (ListingOwnerKind)row.OwnerKind,
            row.AuthorityValidFrom,
            row.AuthorityValidUntil,
            row.CreatedAt,
            row.SubmittedAt,
            row.UpdatedAt,
            row.EvidenceCount,
            row.AllEvidenceClean,
            row.IsCurrentlyPublished,
            decision);
    }

    private static PublicListingPrice Price(ReviewerListingSubmissionReadRow row) =>
        new(row.Amount, row.Currency, (RentPeriod?)row.RentPeriod);

    private static PublicListingLocation Location(ReviewerListingSubmissionReadRow row) =>
        new(row.Country, row.Region, row.City, row.District);

    // A stored value nobody defined is a data fault, never a guess.
    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException("A stored review value is not supported.");
}
