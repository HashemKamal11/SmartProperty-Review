using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The one commit of a write, and the only place a lost race becomes a public error.
/// </summary>
/// <remarks>
/// Everything a handler changed is tracked and committed by a single <c>SaveChangesAsync</c>, which is atomic. Only a
/// <see cref="ListingWriteConflictException"/> of a kind the caller names is translated. Any other kind, and every
/// other exception, propagates untouched: an infrastructure fault is never turned into a user error.
/// </remarks>
internal static class WriteCommit
{
    public static async Task<Error?> SaveAsync(
        IListingSubmissionWriteRepository repository,
        Func<ListingWriteConflictKind, Error?> translate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);

            return null;
        }
        catch (ListingWriteConflictException exception) when (translate(exception.Kind) is not null)
        {
            return translate(exception.Kind);
        }
    }

    public static Error? ForCreate(ListingWriteConflictKind kind)
    {
        return kind is ListingWriteConflictKind.ListingSlot or ListingWriteConflictKind.SubmissionVersion
            ? WriteErrors.OpenRevisionExists
            : null;
    }

    public static Error? ForChange(ListingWriteConflictKind kind)
    {
        return kind == ListingWriteConflictKind.StaleRevision ? WriteErrors.StaleRevision : null;
    }
}
