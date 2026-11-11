using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>
/// Reads of requests and commitments, scoped to one person inside the query itself.
/// </summary>
/// <remarks>
/// <para>
/// A customer's rows are those whose requester column is the caller. An advertiser's rows are those made against a
/// version whose stored authority names the caller as the acting user, compared as exact text on the canonical GUID so
/// that a missing or differently formatted value can never count as ownership. A commitment is visible to its customer
/// and its owner only. Counting, ordering and paging happen in the database; when both kinds are listed together, each
/// kind is paged from the database and the two are merged by time.
/// </para>
/// <para>
/// An unsigned commitment past its signing deadline is shown as Expired whether or not anything has stored that yet.
/// </para>
/// </remarks>
internal sealed class MarketplaceRequestReadRepository(ApplicationDbContext context) : IMarketplaceRequestReadRepository
{
    public Task<RequestPage> ListCustomerRequestsAsync(
        Guid customerUserId,
        RequestKind? kind,
        RequestStatusFilter? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return ListAsync(
            context.PurchaseRequests.AsNoTracking().Where(r => r.BuyerUserId == customerUserId),
            context.RentalRequests.AsNoTracking().Where(r => r.RenterUserId == customerUserId),
            includeRequester: false,
            kind,
            listingId: null,
            status,
            offset,
            limit,
            cancellationToken);
    }

    public async Task<MarketplaceRequestView?> GetCustomerRequestAsync(
        Guid customerUserId,
        RequestKind kind,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var page = await ListAsync(
            context.PurchaseRequests.AsNoTracking().Where(r => r.BuyerUserId == customerUserId && r.Id == requestId),
            context.RentalRequests.AsNoTracking().Where(r => r.RenterUserId == customerUserId && r.Id == requestId),
            includeRequester: false,
            kind,
            listingId: null,
            status: null,
            offset: 0,
            limit: 1,
            cancellationToken);

        return page.Items.FirstOrDefault();
    }

    public Task<RequestPage> ListReceivedRequestsAsync(
        Guid ownerUserId,
        RequestKind? kind,
        Guid? listingId,
        RequestStatusFilter? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return ListAsync(
            ReceivedPurchases(ownerUserId),
            ReceivedRentals(ownerUserId),
            includeRequester: true,
            kind,
            listingId,
            status,
            offset,
            limit,
            cancellationToken);
    }

    public async Task<MarketplaceRequestView?> GetReceivedRequestAsync(
        Guid ownerUserId,
        RequestKind kind,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var page = await ListAsync(
            ReceivedPurchases(ownerUserId).Where(r => r.Id == requestId),
            ReceivedRentals(ownerUserId).Where(r => r.Id == requestId),
            includeRequester: true,
            kind,
            listingId: null,
            status: null,
            offset: 0,
            limit: 1,
            cancellationToken);

        return page.Items.FirstOrDefault();
    }

    public async Task<CommitmentPage> ListCommitmentsAsync(
        DateTimeOffset at,
        Guid userId,
        CommitmentRole role,
        CommitmentStatus? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = context.PropertyCommitments.AsNoTracking()
            .Where(c => role == CommitmentRole.Owner ? c.OwnerUserId == userId : c.CustomerUserId == userId);

        if (status is { } wanted)
        {
            query = wanted switch
            {
                CommitmentStatus.Expired => query.Where(c =>
                    c.Status == CommitmentStatus.Expired
                    || (c.Status == CommitmentStatus.PendingSignature && c.SigningDeadline <= at)),
                CommitmentStatus.PendingSignature => query.Where(c =>
                    c.Status == CommitmentStatus.PendingSignature && c.SigningDeadline > at),
                _ => query.Where(c => c.Status == wanted)
            };
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.AcceptedAt).ThenBy(c => c.Id)
            .Skip(offset).Take(limit).ToListAsync(cancellationToken);

        return new CommitmentPage(rows.Select(row => ToView(row, userId, at)).ToArray(), total);
    }

    public async Task<CommitmentView?> GetCommitmentAsync(
        DateTimeOffset at,
        Guid userId,
        Guid commitmentId,
        CancellationToken cancellationToken = default)
    {
        var row = await context.PropertyCommitments.AsNoTracking()
            .SingleOrDefaultAsync(
                c => c.Id == commitmentId && (c.CustomerUserId == userId || c.OwnerUserId == userId),
                cancellationToken);

        return row is null ? null : ToView(row, userId, at);
    }

    // ---- requests

    private IQueryable<PurchaseRequest> ReceivedPurchases(Guid ownerUserId)
    {
        var actor = ownerUserId.ToString("D");

        return context.PurchaseRequests.FromSqlInterpolated(
            $"""
            SELECT r.* FROM marketplace.purchase_requests AS r
            JOIN marketplace.submissions AS s ON s.id = r.submission_id
            WHERE (s.authority_snapshot->'Payload'->>'ActingUserId') COLLATE "C" = {actor}
            """).AsNoTracking();
    }

    private IQueryable<RentalRequest> ReceivedRentals(Guid ownerUserId)
    {
        var actor = ownerUserId.ToString("D");

        return context.RentalRequests.FromSqlInterpolated(
            $"""
            SELECT r.* FROM marketplace.rental_requests AS r
            JOIN marketplace.submissions AS s ON s.id = r.submission_id
            WHERE (s.authority_snapshot->'Payload'->>'ActingUserId') COLLATE "C" = {actor}
            """).AsNoTracking();
    }

    private async Task<RequestPage> ListAsync(
        IQueryable<PurchaseRequest> purchases,
        IQueryable<RentalRequest> rentals,
        bool includeRequester,
        RequestKind? kind,
        Guid? listingId,
        RequestStatusFilter? status,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (listingId is { } listing)
        {
            purchases = purchases.Where(r => r.ListingId == listing);
            rentals = rentals.Where(r => r.ListingId == listing);
        }

        if (status is { } wanted)
        {
            var purchaseStatus = Enum.Parse<PurchaseRequestStatus>(wanted.ToString());
            var rentalStatus = Enum.Parse<RentalRequestStatus>(wanted.ToString());
            purchases = purchases.Where(r => r.Status == purchaseStatus);
            rentals = rentals.Where(r => r.Status == rentalStatus);
        }

        var wantPurchases = kind is null or RequestKind.Purchase;
        var wantRentals = kind is null or RequestKind.Rental;
        var total = 0L;
        var views = new List<MarketplaceRequestView>();
        var window = offset + limit;

        if (wantPurchases)
        {
            total += await purchases.LongCountAsync(cancellationToken);
            var rows = await purchases.OrderByDescending(r => r.SubmittedAt).ThenBy(r => r.Id).Take(window)
                .Select(r => new
                {
                    Request = r,
                    Name = includeRequester
                        ? context.Users.Where(u => u.Id == r.BuyerUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault()
                        : null
                })
                .ToListAsync(cancellationToken);
            views.AddRange(rows.Select(row => ToView(row.Request, row.Name)));
        }

        if (wantRentals)
        {
            total += await rentals.LongCountAsync(cancellationToken);
            var rows = await rentals.OrderByDescending(r => r.SubmittedAt).ThenBy(r => r.Id).Take(window)
                .Select(r => new
                {
                    Request = r,
                    Name = includeRequester
                        ? context.Users.Where(u => u.Id == r.RenterUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault()
                        : null
                })
                .ToListAsync(cancellationToken);
            views.AddRange(rows.Select(row => ToView(row.Request, row.Name)));
        }

        var page = views.OrderByDescending(view => view.SubmittedAt).ThenBy(view => view.RequestId)
            .Skip(offset).Take(limit).ToArray();

        return new RequestPage(page, total);
    }

    private static MarketplaceRequestView ToView(PurchaseRequest request, string? requesterName)
    {
        return new MarketplaceRequestView(
            RequestKind.Purchase,
            request.Id,
            request.ListingId,
            request.Status.ToString(),
            request.SubmittedAt,
            request.DecidedAt,
            request.OfferedAmount,
            request.Currency,
            PricingPeriod: null,
            StartDate: null,
            EndDate: null,
            CheckInTime: null,
            CheckOutTime: null,
            TimeZoneId: null,
            request.Message,
            requesterName);
    }

    private static MarketplaceRequestView ToView(RentalRequest request, string? requesterName)
    {
        return new MarketplaceRequestView(
            RequestKind.Rental,
            request.Id,
            request.ListingId,
            request.Status.ToString(),
            request.SubmittedAt,
            request.DecidedAt,
            request.Amount,
            request.Currency,
            request.PricingPeriod?.ToString(),
            request.StartDate,
            request.EndDate,
            request.CheckInTime,
            request.CheckOutTime,
            request.TimeZoneId,
            request.Message,
            requesterName);
    }

    private static CommitmentView ToView(PropertyCommitment commitment, Guid viewerUserId, DateTimeOffset at)
    {
        // Nothing stores the expiry until the property is next written, so a reader applies it too.
        var overdue = commitment.IsOverdue(at);

        return new CommitmentView(
            commitment.Id,
            commitment.Kind.ToString(),
            overdue ? CommitmentStatus.Expired.ToString() : commitment.Status.ToString(),
            commitment.ListingId,
            commitment.Amount,
            commitment.Currency,
            commitment.PricingPeriod?.ToString(),
            commitment.StartDate,
            commitment.EndDate,
            commitment.CheckInTime,
            commitment.CheckOutTime,
            commitment.TimeZoneId,
            commitment.AcceptedAt,
            commitment.SigningDeadline,
            commitment.SignedAt,
            commitment.HandedOverAt,
            commitment.CompletedAt,
            overdue ? commitment.SigningDeadline : commitment.ExpiredAt,
            IsOwner: commitment.OwnerUserId == viewerUserId);
    }
}
