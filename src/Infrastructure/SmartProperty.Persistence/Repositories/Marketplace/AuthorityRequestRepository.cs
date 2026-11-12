using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Configurations.Marketplace;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

internal sealed class AuthorityRequestRepository(ApplicationDbContext context) : IAuthorityRequestRepository
{
    private const string ActiveGrantIndexName = "ux_marketplace_authority_grants_active";

    private static readonly AuthorityRequestStatus[] Open =
        [AuthorityRequestStatus.Draft, AuthorityRequestStatus.PendingLegalReview, AuthorityRequestStatus.PendingIssuance];

    private static readonly AuthorityRequestStatus[] Waiting =
        [AuthorityRequestStatus.PendingLegalReview, AuthorityRequestStatus.PendingIssuance];

    public Task<AuthorityRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return context.AuthorityRequests
            .Include(request => request.Documents)
            .Include(request => request.Decisions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<AuthorityRequest>> ListForOwnerAsync(
        Guid ownerUserId, Guid? propertyId, CancellationToken cancellationToken = default)
    {
        var query = context.AuthorityRequests.AsNoTracking()
            .Include(request => request.Documents)
            .Include(request => request.Decisions)
            .AsSplitQuery()
            .Where(request => request.RequesterUserId == ownerUserId);

        if (propertyId is { } property)
        {
            query = query.Where(request => request.PropertyId == property);
        }

        return await query
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AuthorityRequest> Items, int TotalCount)> ListForStaffAsync(
        AuthorityRequestStatus? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = context.AuthorityRequests.AsNoTracking()
            .Where(request => request.Status != AuthorityRequestStatus.Draft);

        if (status is { } wanted)
        {
            query = query.Where(request => request.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);

        // Work waiting for someone comes oldest first, so nothing waits forever; history comes newest first.
        var ordered = status is { } filter && Waiting.Contains(filter)
            ? query.OrderBy(request => request.SubmittedAt).ThenBy(request => request.Id)
            : query.OrderByDescending(request => request.UpdatedAt).ThenByDescending(request => request.Id);

        var items = await ordered
            .Include(request => request.Documents)
            .Include(request => request.Decisions)
            .AsSplitQuery()
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> HasOpenRequestAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        return context.AuthorityRequests.AsNoTracking()
            .AnyAsync(request => request.PropertyId == propertyId && Open.Contains(request.Status), cancellationToken);
    }

    public async Task<IReadOnlyList<PropertyAuthorityGrant>> GetUnrevokedGrantsAsync(
        Guid granteeUserId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        return await context.PropertyAuthorityGrants.AsNoTracking()
            .Where(grant => grant.GranteeUserId == granteeUserId && grant.PropertyId == propertyId && grant.RevokedAt == null)
            .ToListAsync(cancellationToken);
    }

    public Task<PropertyAuthorityGrant?> GetGrantAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        return context.PropertyAuthorityGrants.SingleOrDefaultAsync(grant => grant.Id == grantId, cancellationToken);
    }

    public async Task<IReadOnlyList<PropertyAuthorityGrant>> ListGrantsAsync(
        Guid? propertyId, Guid? granteeUserId, CancellationToken cancellationToken = default)
    {
        var query = context.PropertyAuthorityGrants.AsNoTracking();

        if (propertyId is { } property)
        {
            query = query.Where(grant => grant.PropertyId == property);
        }

        if (granteeUserId is { } grantee)
        {
            query = query.Where(grant => grant.GranteeUserId == grantee);
        }

        return await query
            .OrderByDescending(grant => grant.IssuedAt)
            .ThenByDescending(grant => grant.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthorityDocumentEvidenceRow>> GetCurrentDocumentsAsync(
        Guid requesterUserId, Guid propertyId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var ids = documentIds.ToArray();

        return await (
                from document in context.AuthorityRequestDocuments.AsNoTracking()
                join request in context.AuthorityRequests.AsNoTracking() on document.RequestId equals request.Id
                where ids.Contains(document.DocumentId)
                      && document.SupersededAt == null
                      && document.RemovedAt == null
                      && request.RequesterUserId == requesterUserId
                      && request.PropertyId == propertyId
                select new AuthorityDocumentEvidenceRow(
                    document, request.RequesterUserId, request.PropertyId, request.Status, request.RequestsSale, request.RequestsRent))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthorityDocumentEvidenceRow>> GetDocumentVersionsAsync(
        IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken = default)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }

        var ids = versionIds.ToArray();

        return await (
                from document in context.AuthorityRequestDocuments.AsNoTracking()
                join request in context.AuthorityRequests.AsNoTracking() on document.RequestId equals request.Id
                where ids.Contains(document.Id)
                select new AuthorityDocumentEvidenceRow(
                    document, request.RequesterUserId, request.PropertyId, request.Status, request.RequestsSale, request.RequestsRent))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListDueScansAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default)
    {
        return await context.AuthorityRequestDocuments.AsNoTracking()
            .Where(document => document.ScanStatus == DocumentScanStatus.PendingScan && document.NextScanAt <= now)
            .OrderBy(document => document.NextScanAt)
            .ThenBy(document => document.Id)
            .Select(document => document.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ProcessLockedScanAsync(
        Guid versionId, Func<AuthorityRequestDocument?, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Another worker scanning the same version holds the row; this one skips it instead of waiting.
            var document = await context.AuthorityRequestDocuments
                .FromSqlInterpolated($"""
                    SELECT * FROM marketplace.authority_request_documents
                    WHERE id = {versionId} AND scan_status = 'PendingScan'
                    FOR UPDATE SKIP LOCKED
                    """)
                .SingleOrDefaultAsync(cancellationToken);

            await work(document);

            if (document is not null)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();

            return false;
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    public void Add(AuthorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        context.AuthorityRequests.Add(request);
    }

    public void AddGrant(PropertyAuthorityGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        context.PropertyAuthorityGrants.Add(grant);
    }

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken = default)
    {
        TrackNewChildren();

        try
        {
            await context.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return false;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AuthorityRequestConfiguration.OpenRequestUniqueIndexName
                or AuthorityRequestDocumentConfiguration.CurrentVersionUniqueIndexName
                or ActiveGrantIndexName
        })
        {
            context.ChangeTracker.Clear();

            return false;
        }
    }

    /// <summary>
    /// Versions and decisions carry their ids from the Domain, so EF would take one found only through a navigation for
    /// an existing row. Every child not yet tracked is new: they are only ever appended.
    /// </summary>
    private void TrackNewChildren()
    {
        // Enumerating entries would otherwise run change detection first and classify the new children itself.
        var autoDetect = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            AddUntrackedChildren();
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private void AddUntrackedChildren()
    {
        foreach (var entry in context.ChangeTracker.Entries<AuthorityRequest>().ToArray())
        {
            foreach (var document in entry.Entity.Documents)
            {
                if (context.Entry(document).State == EntityState.Detached)
                {
                    context.Entry(document).State = EntityState.Added;
                }
            }

            foreach (var decision in entry.Entity.Decisions)
            {
                if (context.Entry(decision).State == EntityState.Detached)
                {
                    context.Entry(decision).State = EntityState.Added;
                }
            }
        }
    }
}
