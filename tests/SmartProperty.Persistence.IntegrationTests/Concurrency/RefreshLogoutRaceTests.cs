using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Common.Results;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// A refresh and a logout present the same token at the same time, against real PostgreSQL.
/// </summary>
/// <remarks>
/// Synchronised the same way as the same-token refresh race: a <see cref="SaveChangesBarrier"/> for two
/// participants holds both handlers at <c>SavingChangesAsync</c> until both have loaded the active token, then
/// releases them into two real, competing saves. Each handler runs in its own scope with its own context.
///
/// Which one wins is left to the database, because production promises no ordering between them. What these
/// tests assert is the set of invariants that must hold whichever way it goes — and, separately, that each
/// outcome is internally consistent: a successful refresh leaves a replacement, a losing refresh leaves none.
/// </remarks>
public sealed class RefreshLogoutRaceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PresentedToken = "test-presented-refresh-token";

    private readonly SaveChangesBarrier barrier = new(participants: 2);

    [Fact]
    public async Task LogoutAlwaysReportsSuccessWhicheverWriterWins()
    {
        await SeedActiveTokenAsync();

        var (_, logout) = await RaceAsync();

        // Logout is idempotent by design: losing the race still destroyed the credential, which is what it
        // set out to do.
        Assert.True(logout.IsSuccess);
    }

    [Fact]
    public async Task TheOriginalTokenEndsRevokedWhicheverWriterWins()
    {
        var originalTokenHash = await SeedActiveTokenAsync();

        await RaceAsync();

        await using var verifying = Host.CreateVerificationContext();
        var original = await verifying.RefreshTokens.SingleAsync(token => token.TokenHash == originalTokenHash);

        Assert.NotNull(original.RevokedAt);
        Assert.False(original.IsActive(TestClock.DefaultNow.AddMinutes(5)));
    }

    [Fact]
    public async Task AtMostOneReplacementTokenSurvives()
    {
        var originalTokenHash = await SeedActiveTokenAsync();

        var (refresh, _) = await RaceAsync();

        await using var verifying = Host.CreateVerificationContext();
        var replacements = await verifying.RefreshTokens
            .Where(token => token.TokenHash != originalTokenHash)
            .ToListAsync();

        // Never two. Either the refresh won and rotated, or it lost and its insert was rolled back with it.
        Assert.Equal(refresh.IsSuccess ? 1 : 0, replacements.Count);

        if (refresh.IsSuccess)
        {
            Assert.Equal(Host.Tokens.HashRefreshToken(refresh.Value.RefreshToken), replacements[0].TokenHash);
            Assert.Null(replacements[0].RevokedAt);
        }
    }

    [Fact]
    public async Task ALosingRefreshReportsTheSameInvalidResultAsAReplayedToken()
    {
        await SeedActiveTokenAsync();

        var (refresh, _) = await RaceAsync();

        if (refresh.IsSuccess)
        {
            // The refresh won this run. Its outcome is covered by the assertions above; there is no losing
            // result to inspect, and inventing an ordering guarantee production does not make would be wrong.
            return;
        }

        Assert.NotNull(refresh.Error);
        Assert.Equal("authentication.invalid_refresh_token", refresh.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, refresh.Error.Type);
        Assert.DoesNotContain("concurren", refresh.Error.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRaceNeverPersistsARawTokenValue()
    {
        await SeedActiveTokenAsync();

        await RaceAsync();

        await using var verifying = Host.CreateVerificationContext();
        var storedHashes = await verifying.RefreshTokens.Select(token => token.TokenHash).ToListAsync();

        Assert.DoesNotContain(PresentedToken, storedHashes);
    }

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [barrier];
    }

    private async Task<(Result<RefreshResult> Refresh, Result Logout)> RaceAsync()
    {
        var refresh = RefreshAsync();
        var logout = LogoutAsync();

        await Task.WhenAll(refresh, logout);

        return (await refresh, await logout);
    }

    private async Task<Result<RefreshResult>> RefreshAsync()
    {
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<RefreshCommandHandler>()
            .Handle(new RefreshCommand(PresentedToken));
    }

    private async Task<Result> LogoutAsync()
    {
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LogoutCommandHandler>()
            .Handle(new LogoutCommand(PresentedToken));
    }

    private async Task<string> SeedActiveTokenAsync()
    {
        var tokenHash = Host.Tokens.HashRefreshToken(PresentedToken);

        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();

        seeding.Users.Add(user);
        seeding.RefreshTokens.Add(AccessGraph.ActiveRefreshToken(user.Id, tokenHash));

        await seeding.SaveChangesAsync();

        return tokenHash;
    }
}
