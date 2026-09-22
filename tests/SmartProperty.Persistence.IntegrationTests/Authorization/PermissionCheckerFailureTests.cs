using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Authorization;

/// <summary>
/// A database failure is not an authorization answer: it must propagate, never become
/// <see cref="AuthorizationDecision.Denied"/>.
/// </summary>
/// <remarks>
/// The failure is produced by pointing the checker at a database that does not exist on the suite's own
/// container. PostgreSQL rejects the connection with <c>3D000 invalid_catalog_name</c>, which is a real
/// infrastructure failure arriving through the real Npgsql provider and the real query.
///
/// Nothing is stopped, killed, or removed to arrange it. The container, its network, and every other test
/// database stay untouched — which is also why this is safe to keep in the permanent suite.
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
public sealed class PermissionCheckerFailureTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task AnUnreachableDatabaseSurfacesAsAnExceptionRatherThanADenial()
    {
        await using var host = PersistenceTestHost.Create(fixture.BuildMissingDatabaseConnectionString());

        using var scope = host.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

        var request = AuthorizationRequest.For(
            Guid.NewGuid(),
            "property.read",
            AuthorizationTarget.Platform);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => checker.CheckAsync(request));

        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, exception.SqlState);
    }

    [Fact]
    public async Task AnUnreachableDatabaseFailsTheWorkspacePathToo()
    {
        await using var host = PersistenceTestHost.Create(fixture.BuildMissingDatabaseConnectionString());

        using var scope = host.CreateScope();
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

        var request = AuthorizationRequest.For(
            Guid.NewGuid(),
            "property.read",
            AuthorizationTarget.Workspace(Guid.NewGuid()));

        await Assert.ThrowsAsync<PostgresException>(() => checker.CheckAsync(request));
    }
}
