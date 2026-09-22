using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Proves the refresh-token optimistic concurrency mapping works against real PostgreSQL, and that the
/// persistence boundary turns the loss into the provider-neutral exception the Application layer catches.
/// </summary>
/// <remarks>
/// The concurrency token is <c>RefreshToken.RevokedAt</c>, configured with <c>IsConcurrencyToken()</c> in
/// <c>RefreshTokenConfiguration</c>. There is no xmin mapping and no version column: EF puts the value it loaded
/// into the UPDATE predicate, so a writer that loaded <c>revoked_at IS NULL</c> updates zero rows once another
/// writer has set it.
///
/// Every test uses two independent contexts that load the row separately. Nothing is shared, nothing sleeps, and
/// the order is fixed by the test rather than by timing: the first save is awaited to completion before the
/// second one starts, which is the strictest form of the race and the one whose outcome is not in doubt.
/// </remarks>
public sealed class RefreshTokenConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string TokenHash = "test-token-hash-concurrency";

    [Fact]
    public async Task TheStaleWriterLosesWithAConcurrencyException()
    {
        await SeedActiveTokenAsync();

        await using var contextA = Host.CreateVerificationContext();
        await using var contextB = Host.CreateVerificationContext();

        var tokenSeenByA = await contextA.RefreshTokens.SingleAsync();
        var tokenSeenByB = await contextB.RefreshTokens.SingleAsync();

        // Both loaded the same row while it was still active.
        Assert.Null(tokenSeenByA.RevokedAt);
        Assert.Null(tokenSeenByB.RevokedAt);

        tokenSeenByA.Revoke(TestClock.DefaultNow.AddMinutes(1));
        tokenSeenByB.Revoke(TestClock.DefaultNow.AddMinutes(2));

        Assert.Equal(1, await contextA.SaveChangesAsync());

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        var failed = Assert.Single(exception.Entries);
        Assert.Equal(typeof(RefreshToken), failed.Metadata.ClrType);
    }

    [Fact]
    public async Task TheWinnerIsTheOnlyRevocationThatReachesTheDatabase()
    {
        await SeedActiveTokenAsync();

        await using (var contextA = Host.CreateVerificationContext())
        await using (var contextB = Host.CreateVerificationContext())
        {
            var tokenSeenByA = await contextA.RefreshTokens.SingleAsync();
            var tokenSeenByB = await contextB.RefreshTokens.SingleAsync();

            tokenSeenByA.Revoke(TestClock.DefaultNow.AddMinutes(1));
            tokenSeenByB.Revoke(TestClock.DefaultNow.AddMinutes(2));

            await contextA.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());
        }

        await using var verifying = Host.CreateVerificationContext();
        var persisted = await verifying.RefreshTokens.SingleAsync();

        Assert.Equal(TestClock.DefaultNow.AddMinutes(1), persisted.RevokedAt);
    }

    [Fact]
    public async Task TheStaleWriterReachesTheApplicationAsAConcurrencyConflict()
    {
        await SeedActiveTokenAsync();

        using var scopeA = Host.CreateScope();
        using var scopeB = Host.CreateScope();

        var tokenSeenByA = await LoadAsync(scopeA);
        var tokenSeenByB = await LoadAsync(scopeB);

        tokenSeenByA.Revoke(TestClock.DefaultNow.AddMinutes(1));
        tokenSeenByB.Revoke(TestClock.DefaultNow.AddMinutes(2));

        await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.Equal(PersistenceResource.RefreshToken, exception.Resource);
        Assert.IsType<DbUpdateConcurrencyException>(exception.InnerException);
    }

    [Fact]
    public async Task ARevocationOfAnUntouchedTokenStillSucceeds()
    {
        // The concurrency predicate must not reject an ordinary, uncontested revocation.
        await SeedActiveTokenAsync();

        using (var scope = Host.CreateScope())
        {
            var token = await LoadAsync(scope);
            token.Revoke(TestClock.DefaultNow.AddMinutes(1));

            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var verifying = Host.CreateVerificationContext();

        Assert.Equal(TestClock.DefaultNow.AddMinutes(1), (await verifying.RefreshTokens.SingleAsync()).RevokedAt);
    }

    private static async Task<RefreshToken> LoadAsync(IServiceScope scope)
    {
        var token = await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
            .GetByTokenHashAsync(TokenHash);

        Assert.NotNull(token);

        return token;
    }

    private async Task SeedActiveTokenAsync()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();

        seeding.Users.Add(user);
        seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, TokenHash));

        await seeding.SaveChangesAsync();
    }
}
