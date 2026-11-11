using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Entities.Marketplace;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>
/// The write side of listing submissions: one transaction per unit of work, locks in one fixed order, and one commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order.</b> The registry property row is locked <c>FOR UPDATE</c> first, which also opens the transaction.
/// The grant row is then read <c>FOR SHARE</c> (revoking a grant takes it exclusively), then the listing row
/// <c>FOR UPDATE</c>, then submission rows <c>FOR UPDATE</c>. A call that needs a lock the caller has not taken yet
/// throws instead of taking it out of order, so no two writers can wait on each other.
/// </para>
/// <para>
/// <b>Fresh reads.</b> A submission read before the property is locked is not tracked: it only tells the caller which
/// property to lock. Every decision uses the read made under the locks, so a stale copy can never be the one that is
/// changed.
/// </para>
/// <para>
/// <b>Atomic.</b> Changes are tracked, not flushed. <see cref="SaveChangesAsync"/> writes everything, including a
/// discard's audit row, and commits in the same transaction, or writes nothing. A lost race on a unique constraint
/// becomes <see cref="ListingWriteConflictException"/>. A unit of work that is abandoned before saving is rolled back
/// when the repository is disposed with its scope.
/// </para>
/// <para>
/// This class reads grants; it never creates or revokes one.
/// </para>
/// </remarks>
internal sealed class ListingSubmissionWriteRepository(ApplicationDbContext context)
    : IMarketplaceRequestWriteRepository, IListingSubmissionAuditRecorder, IAsyncDisposable, IDisposable
{
    private const string ListingSlotConstraint = "ux_marketplace_listings_property_purpose";
    private const string SubmissionVersionConstraint = "ux_marketplace_submissions_listing_version";
    private const string PurchaseRequestOpenConstraint = "ux_marketplace_purchase_requests_open";
    private const string RentalRequestOpenConstraint = "ux_marketplace_rental_requests_open";
    private const string RentalOverlapConstraint = "ex_marketplace_commitments_rental_overlap";
    private const string SingleSaleConstraint = "ex_marketplace_commitments_single_sale";

    private IDbContextTransaction? _transaction;
    private Guid? _lockedPropertyId;
    private readonly HashSet<ListingPurpose> _grantsRead = [];

    public async Task<LockedPropertyView?> LockPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (_lockedPropertyId is { } held && held != propertyId)
        {
            // One unit of work locks one property, so there is only ever one property lock to order.
            throw new InvalidOperationException("A write unit locks exactly one property.");
        }

        await EnsureTransactionAsync(cancellationToken);

        await using var command = CreateCommand(
            """
            -- A property made from the place tree has no region (the tree is country, city, area). The listing's location
            -- needs a region label, so the property's own recorded city is used for it. This is a label for the listing's
            -- frozen location snapshot only: the property row is never written, an explicit region always wins, a blank
            -- region counts as missing, and nothing else (district, country, text guesses) is ever substituted. If a real
            -- region is recorded later, the snapshot no longer matches and the listing is refused as stale.
            SELECT p.id, p.status, p.type, p.address_country_code,
                   COALESCE(NULLIF(btrim(p.address_region), ''), NULLIF(btrim(p.address_city), '')) AS region,
                   p.address_city, p.address_district,
                   EXISTS (SELECT 1 FROM marketplace.property_reservations AS r WHERE r.property_id = p.id),
                   COALESCE(p.time_zone_id, co.time_zone_id)
            FROM registry.properties AS p
            LEFT JOIN locations.areas AS a ON a.id = p.area_id
            LEFT JOIN locations.cities AS ci ON ci.id = a.city_id
            LEFT JOIN locations.countries AS co ON co.id = ci.country_id
            WHERE p.id = @id
            FOR UPDATE OF p;
            """,
            ("id", propertyId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var view = new LockedPropertyView(
            reader.GetGuid(0),
            IsActive: reader.GetString(1) == nameof(PropertyStatus.Active),
            HasMarketplaceReservation: reader.GetBoolean(7),
            Enum.Parse<PropertyType>(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(8) ? null : reader.GetString(8));

        _lockedPropertyId = propertyId;

        return view;
    }

    public async Task<AuthorityGrantView?> GetGrantForSharedLockAsync(
        Guid granteeUserId,
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        RequirePropertyLock(propertyId);

        // A grant in force wins; otherwise the newest revoked one is returned, so a revoked grant is reported as
        // revoked rather than as missing. The shared lock keeps a revocation from committing while this unit runs.
        var purposeText = purpose.ToString();
        var grant = await context.PropertyAuthorityGrants
            .FromSqlInterpolated(
                $"""
                SELECT * FROM marketplace.authority_grants
                WHERE grantee_user_id = {granteeUserId} AND property_id = {propertyId} AND purpose = {purposeText}
                ORDER BY (revoked_at IS NULL) DESC, issued_at DESC
                LIMIT 1
                FOR SHARE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        _grantsRead.Add(purpose);

        return grant is null ? null : ToView(grant);
    }

    public async Task<PropertyListing?> GetListingAsync(
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        RequirePropertyLock(propertyId);

        if (!_grantsRead.Contains(purpose))
        {
            throw new InvalidOperationException("The grant must be read before the listing is locked.");
        }

        await using (var command = CreateCommand(
            "SELECT id FROM marketplace.listings WHERE property_id = @property AND purpose = @purpose FOR UPDATE;",
            ("property", propertyId),
            ("purpose", purpose.ToString())))
        {
            await command.ExecuteScalarAsync(cancellationToken);
        }

        return await context.PropertyListings
            .SingleOrDefaultAsync(listing => listing.PropertyId == propertyId && listing.Purpose == purpose, cancellationToken);
    }

    public Task AddListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        RequirePropertyLock(listing.PropertyId);
        context.PropertyListings.Add(listing);

        return Task.CompletedTask;
    }

    public async Task<ListingSubmission?> GetLatestSubmissionAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        RequireAnyPropertyLock();

        Guid? latestId;
        await using (var command = CreateCommand(
            "SELECT id FROM marketplace.submissions WHERE listing_id = @listing ORDER BY version DESC LIMIT 1 FOR UPDATE;",
            ("listing", listingId)))
        {
            latestId = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        return latestId is { } id
            ? await context.ListingSubmissions.Include(s => s.ReviewDecision).SingleAsync(s => s.Id == id, cancellationToken)
            : null;
    }

    public async Task<ListingSubmission?> GetOwnSubmissionAsync(
        Guid submissionId,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty || actingUserId == Guid.Empty)
        {
            return null;
        }

        // The owner is the acting user of the stored authority. The comparison is exact text equality on canonical
        // GUID text, so a missing, malformed or differently formatted value can never count as ownership.
        var actor = actingUserId.ToString("D");
        var owned = context.ListingSubmissions.FromSqlInterpolated(
            $"""
            SELECT * FROM marketplace.submissions
            WHERE id = {submissionId} AND (authority_snapshot->'Payload'->>'ActingUserId') COLLATE "C" = {actor}
            """);

        if (_lockedPropertyId is null)
        {
            // Before the property is locked this read only names the property to lock. It is not tracked, so the read
            // made under the locks cannot be answered from it.
            return await owned.AsNoTracking().Include(s => s.ReviewDecision).SingleOrDefaultAsync(cancellationToken);
        }

        await using (var command = CreateCommand(
            "SELECT id FROM marketplace.submissions WHERE id = @id FOR UPDATE;",
            ("id", submissionId)))
        {
            await command.ExecuteScalarAsync(cancellationToken);
        }

        return await owned.Include(s => s.ReviewDecision).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ListingSubmission?> GetSubmissionAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty)
        {
            return null;
        }

        var byId = context.ListingSubmissions.Include(s => s.ReviewDecision);

        if (_lockedPropertyId is null)
        {
            return await byId.AsNoTracking().SingleOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        }

        await using (var command = CreateCommand(
            "SELECT id FROM marketplace.submissions WHERE id = @id FOR UPDATE;",
            ("id", submissionId)))
        {
            await command.ExecuteScalarAsync(cancellationToken);
        }

        return await byId.SingleOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
    }

    public async Task<PropertyListing?> FindListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        return listingId == Guid.Empty
            ? null
            : await context.PropertyListings.AsNoTracking().SingleOrDefaultAsync(l => l.Id == listingId, cancellationToken);
    }

    public async Task<PropertyListing?> GetListingByIdAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        var known = await FindListingAsync(listingId, cancellationToken);

        if (known is null)
        {
            return null;
        }

        RequirePropertyLock(known.PropertyId);

        if (!_grantsRead.Contains(known.Purpose))
        {
            throw new InvalidOperationException("The grant must be read before the listing is locked.");
        }

        await using (var command = CreateCommand("SELECT id FROM marketplace.listings WHERE id = @id FOR UPDATE;", ("id", listingId)))
        {
            await command.ExecuteScalarAsync(cancellationToken);
        }

        return await context.PropertyListings.SingleOrDefaultAsync(l => l.Id == listingId, cancellationToken);
    }

    // ---- requests and commitments

    public async Task<PurchaseRequest?> FindPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return requestId == Guid.Empty
            ? null
            : await context.PurchaseRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);
    }

    public async Task<RentalRequest?> FindRentalRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return requestId == Guid.Empty
            ? null
            : await context.RentalRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);
    }

    public async Task<PropertyCommitment?> FindCommitmentAsync(Guid commitmentId, CancellationToken cancellationToken = default)
    {
        return commitmentId == Guid.Empty
            ? null
            : await context.PropertyCommitments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == commitmentId, cancellationToken);
    }

    public async Task<PurchaseRequest?> GetPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        RequireAnyPropertyLock();

        await LockRowAsync("marketplace.purchase_requests", requestId, cancellationToken);
        var request = await context.PurchaseRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

        return request is not null && request.PropertyId == _lockedPropertyId ? request : null;
    }

    public async Task<RentalRequest?> GetRentalRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        RequireAnyPropertyLock();

        await LockRowAsync("marketplace.rental_requests", requestId, cancellationToken);
        var request = await context.RentalRequests.SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken);

        return request is not null && request.PropertyId == _lockedPropertyId ? request : null;
    }

    public async Task<PropertyCommitment?> GetCommitmentAsync(Guid commitmentId, CancellationToken cancellationToken = default)
    {
        RequireAnyPropertyLock();

        await LockRowAsync("marketplace.property_commitments", commitmentId, cancellationToken);
        var commitment = await context.PropertyCommitments.SingleOrDefaultAsync(c => c.Id == commitmentId, cancellationToken);

        return commitment is not null && commitment.PropertyId == _lockedPropertyId ? commitment : null;
    }

    public async Task<IReadOnlyList<PurchaseRequest>> GetOpenPurchaseRequestsAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        RequirePropertyLock(propertyId);

        await LockRowsAsync(
            "SELECT id FROM marketplace.purchase_requests WHERE property_id = @property AND status = 'Submitted' ORDER BY id FOR UPDATE;",
            propertyId, cancellationToken);

        return await context.PurchaseRequests
            .Where(r => r.PropertyId == propertyId && r.Status == PurchaseRequestStatus.Submitted)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalRequest>> GetOpenRentalRequestsAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        RequirePropertyLock(propertyId);

        await LockRowsAsync(
            "SELECT id FROM marketplace.rental_requests WHERE property_id = @property AND status = 'Submitted' ORDER BY id FOR UPDATE;",
            propertyId, cancellationToken);

        return await context.RentalRequests
            .Where(r => r.PropertyId == propertyId && r.Status == RentalRequestStatus.Submitted)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PropertyCommitment>> GetOpenCommitmentsAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        RequirePropertyLock(propertyId);

        await LockRowsAsync(
            "SELECT id FROM marketplace.property_commitments WHERE property_id = @property AND status IN ('PendingSignature', 'Signed', 'Occupied') ORDER BY id FOR UPDATE;",
            propertyId, cancellationToken);

        return await context.PropertyCommitments
            .Where(c => c.PropertyId == propertyId
                && (c.Status == CommitmentStatus.PendingSignature || c.Status == CommitmentStatus.Signed || c.Status == CommitmentStatus.Occupied))
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public Task AddPurchaseRequestAsync(PurchaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePropertyLock(request.PropertyId);
        context.PurchaseRequests.Add(request);

        return Task.CompletedTask;
    }

    public Task AddRentalRequestAsync(RentalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePropertyLock(request.PropertyId);
        context.RentalRequests.Add(request);

        return Task.CompletedTask;
    }

    public Task AddCommitmentAsync(PropertyCommitment commitment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        RequirePropertyLock(commitment.PropertyId);
        context.PropertyCommitments.Add(commitment);

        return Task.CompletedTask;
    }

    private async Task LockRowAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand($"SELECT id FROM {table} WHERE id = @id FOR UPDATE;", ("id", id));
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task LockRowsAsync(string sql, Guid propertyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql, ("property", propertyId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
        }
    }

    public Task AddSubmissionAsync(ListingSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        RequirePropertyLock(submission.PropertyId);
        context.ListingSubmissions.Add(submission);

        return Task.CompletedTask;
    }

    public Task RemoveDraftAsync(ListingSubmission draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RequirePropertyLock(draft.PropertyId);
        context.ListingSubmissions.Remove(draft);

        return Task.CompletedTask;
    }

    public async Task<bool> CanRemoveListingAfterDraftAsync(
        Guid listingId,
        Guid draftSubmissionId,
        CancellationToken cancellationToken = default)
    {
        RequireAnyPropertyLock();

        var listing = await context.PropertyListings.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == listingId, cancellationToken);

        if (listing is null
            || listing.PublicationStatus != ListingPublicationStatus.Unpublished
            || listing.PublishedSubmissionId is not null
            || listing.WithdrawnAt is not null
            || listing.AvailabilityStatus != ListingAvailabilityStatus.Open
            || listing.ClosedByReservation)
        {
            return false;
        }

        if (await context.ListingSubmissions.AnyAsync(s => s.ListingId == listingId && s.Id != draftSubmissionId, cancellationToken))
        {
            return false;
        }

        // Nothing may point at the listing: a request or a reservation keeps it.
        return !await context.PurchaseRequests.AnyAsync(r => r.ListingId == listingId, cancellationToken)
            && !await context.RentalRequests.AnyAsync(r => r.ListingId == listingId, cancellationToken)
            && !await context.PropertyMarketplaceReservations.AnyAsync(r => r.WinningListingId == listingId, cancellationToken);
    }

    public Task RemoveListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        RequirePropertyLock(listing.PropertyId);
        context.PropertyListings.Remove(listing);

        return Task.CompletedTask;
    }

    public Task RecordAsync(ListingDraftDiscardedAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        RequirePropertyLock(auditEvent.PropertyId);

        // The record describes the draft being removed in this same unit of work, and nothing else.
        var removed = context.ChangeTracker.Entries<ListingSubmission>()
            .SingleOrDefault(entry => entry.State == EntityState.Deleted && entry.Entity.Id == auditEvent.SubmissionId)
            ?? throw new InvalidOperationException("A discard is recorded only for the draft removed in the same unit of work.");

        context.Set<ListingDraftDiscardAudit>().Add(
            ListingDraftDiscardAudit.Prepare(removed.Entity, removed.Entity.Revision, auditEvent));

        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);

            if (_transaction is not null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException exception) when (exception.Entries.Count > 0
            && exception.Entries.All(entry => entry.Entity is
                ListingSubmission or PropertyListing or PurchaseRequest or RentalRequest or PropertyCommitment))
        {
            await AbandonAsync();

            throw new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && ConflictOf(postgres) is { } kind)
        {
            await AbandonAsync();

            throw new ListingWriteConflictException(kind);
        }
        catch (PostgresException postgres) when (ConflictOf(postgres) is { } kind)
        {
            // The overlap and single-sale rules are deferred to commit, so the database may refuse them only then.
            await AbandonAsync();

            throw new ListingWriteConflictException(kind);
        }
        catch
        {
            await AbandonAsync();

            throw;
        }
        finally
        {
            await ReleaseTransactionAsync();
        }
    }

    // The only constraint violations that are a lost race rather than a fault, by the constraint that lost.
    private static ListingWriteConflictKind? ConflictOf(PostgresException exception)
    {
        return (exception.SqlState, exception.ConstraintName) switch
        {
            (PostgresErrorCodes.UniqueViolation, ListingSlotConstraint) => ListingWriteConflictKind.ListingSlot,
            (PostgresErrorCodes.UniqueViolation, SubmissionVersionConstraint) => ListingWriteConflictKind.SubmissionVersion,
            (PostgresErrorCodes.UniqueViolation, PurchaseRequestOpenConstraint or RentalRequestOpenConstraint)
                => ListingWriteConflictKind.RequestAlreadyOpen,
            (PostgresErrorCodes.ExclusionViolation, RentalOverlapConstraint) => ListingWriteConflictKind.DatesUnavailable,
            (PostgresErrorCodes.ExclusionViolation, SingleSaleConstraint) => ListingWriteConflictKind.PropertyAlreadyCommitted,
            _ => null
        };
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseTransactionAsync();
    }

    // A scope disposed synchronously (a test, a background caller) must not throw because this type owns a
    // transaction. An abandoned unit of work is rolled back when its transaction is disposed.
    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _lockedPropertyId = null;
        _grantsRead.Clear();
    }

    // ---- ordering and transaction

    private void RequirePropertyLock(Guid propertyId)
    {
        if (_lockedPropertyId != propertyId)
        {
            throw new InvalidOperationException("The property lock must be taken first, and for this property.");
        }
    }

    private void RequireAnyPropertyLock()
    {
        if (_lockedPropertyId is null)
        {
            throw new InvalidOperationException("The property lock must be taken first.");
        }
    }

    private async Task EnsureTransactionAsync(CancellationToken cancellationToken)
    {
        _transaction ??= context.Database.CurrentTransaction
            ?? await context.Database.BeginTransactionAsync(cancellationToken);
    }

    // Drops what was tracked, so a refused unit of work leaves nothing behind in the context.
    private async Task AbandonAsync()
    {
        context.ChangeTracker.Clear();

        if (_transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // A commit the server refused has already ended the transaction; there is nothing left to roll back.
            }
        }
    }

    private async Task ReleaseTransactionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        _lockedPropertyId = null;
        _grantsRead.Clear();
    }

    private DbCommand CreateCommand(string sql, params (string Name, object Value)[] parameters)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction?.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        return command;
    }

    private static AuthorityGrantView ToView(PropertyAuthorityGrant grant)
    {
        var authority = grant.Authority;
        var issuerEstablished = grant.Issuer.IdentitiesVerified == true && grant.Issuer.PlatformReviewAuthorized == true;

        return new AuthorityGrantView(
            grant.Id,
            grant.Version,
            grant.GranteeUserId,
            grant.PropertyId,
            AllowsSale: grant.Purpose == ListingPurpose.Sale,
            AllowsRent: grant.Purpose == ListingPurpose.Rent,
            authority.DeclaredOwner.Kind,
            authority.DeclaredOwner.LegalPartyReferenceId,
            authority.DeclaredOwner.LinkedUserId,
            authority.Representation,
            authority.ValidFrom,
            // A grant without an end is malformed; the default value makes the evaluator refuse it.
            authority.ValidUntil ?? default,
            grant.RevokedAt is null ? AuthorityGrantState.Active : AuthorityGrantState.Revoked,
            grant.RevokedAt,
            grant.IssuedByUserId,
            grant.IssuedAt,
            issuerEstablished,
            grant.LegalReviewReferenceId,
            grant.LegalReviewedByUserId,
            grant.LegalReviewedAt);
    }
}
