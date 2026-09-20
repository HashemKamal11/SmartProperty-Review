namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Provider-neutral identifiers for rows whose concurrent modification a use case can handle as an expected
/// outcome. Distinct from <see cref="PersistenceConstraint"/>: a constraint violation means the data was
/// rejected, while a concurrency conflict means another writer changed the row first.
/// </summary>
public enum PersistenceResource
{
    /// <summary>
    /// A refresh token row whose revocation state changed between loading it and saving the rotation.
    /// </summary>
    RefreshToken = 1
}
