using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Marketplace;

/// <summary>
/// The candidates for automatic relisting, read without locks. Every condition here is checked again under the
/// property lock by the relisting, so a stale candidate is simply refused there.
/// </summary>
internal sealed class AutomaticRelistQueue(ApplicationDbContext context) : IAutomaticRelistQueue
{
    public async Task<IReadOnlyList<Guid>> GetDueListingIdsAsync(
        DateTimeOffset at,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            return [];
        }

        return await context.Database
            .SqlQuery<Guid>(
                $"""
                SELECT l.id AS "Value"
                FROM marketplace.listings AS l
                JOIN marketplace.property_commitments AS c ON c.id = l.closed_by_commitment_id
                JOIN marketplace.submissions AS p ON p.id = l.published_submission_id
                WHERE l.relist_automatically
                  AND l.publication_status = 'Withdrawn'
                  AND l.availability_status = 'Closed'
                  AND NOT l.closed_by_reservation
                  AND (c.status = 'Expired'
                       OR (c.status = 'PendingSignature' AND c.signing_deadline <= {at})
                       OR (c.status = 'Completed' AND c.kind = 'Rental'))
                  AND NOT EXISTS (
                      SELECT 1 FROM marketplace.submissions AS s
                      WHERE s.listing_id = l.id AND s.version > p.version)
                ORDER BY l.withdrawn_at, l.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);
    }
}
