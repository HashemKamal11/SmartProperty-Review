using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory permissions, looked up by code exactly the way the real repository compares them: trimmed,
/// case-sensitive. A test that relied on case-insensitive matching would be asserting behaviour the database does
/// not have.
/// </summary>
internal sealed class FakePermissionRepository : IPermissionRepository
{
    private readonly Dictionary<Guid, Permission> _permissionsById = [];
    private readonly List<Permission> _added = [];

    public IReadOnlyList<Permission> Added => _added;

    public void Seed(Permission permission)
    {
        _permissionsById[permission.Id] = permission;
    }

    public Task<Permission?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_permissionsById.GetValueOrDefault(id));
    }

    public Task<Permission?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalizedCode = code.Trim();

        return Task.FromResult(_permissionsById.Values.FirstOrDefault(
            permission => string.Equals(permission.Code, normalizedCode, StringComparison.Ordinal)));
    }

    public Task AddAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        _added.Add(permission);
        Seed(permission);

        return Task.CompletedTask;
    }
}
