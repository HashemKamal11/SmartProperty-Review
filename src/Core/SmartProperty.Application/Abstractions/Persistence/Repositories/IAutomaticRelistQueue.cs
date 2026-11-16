namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Finds the listings that are due to be relisted automatically. A read without locks: it only proposes candidates,
/// and each one is decided again under the locks by the relisting itself.
/// </summary>
public interface IAutomaticRelistQueue
{
    /// <summary>
    /// Listings whose owner chose automatic relisting, that were closed by an accepted request that has since ended
    /// without a lasting deal (signature deadline passed at <paramref name="at"/>, or rental returned), and that have
    /// nothing newer than their last approved version. At most <paramref name="limit"/>, oldest closure first.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetDueListingIdsAsync(DateTimeOffset at, int limit, CancellationToken cancellationToken = default);
}
