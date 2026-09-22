using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Common.Results;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Two requests present the same refresh token at the same time, against real PostgreSQL, through the real
/// <see cref="RefreshCommandHandler"/>.
/// </summary>
/// <remarks>
/// Each competing call runs in its own service scope, so it has its own <c>ApplicationDbContext</c>, its own
/// repositories, and its own unit of work — exactly the isolation two HTTP requests would have. Nothing about
/// the database or the concurrency failure is faked: the only test-owned substitution is the token provider,
/// which hashes deterministically so the test can present a known token without configuring JWT signing.
///
/// <para><b>How the race is made deterministic.</b> A <see cref="SaveChangesBarrier"/> for two participants is
/// installed as an EF <c>SaveChangesInterceptor</c> on the contexts these scopes resolve. Each handler reads the
/// refresh token before it writes, so arriving at <c>SavingChangesAsync</c> proves the row was already loaded.
/// The barrier releases nobody until both have arrived, which pins the state the race requires — both loaded the
/// same active token, neither has sent its UPDATE — and then lets both real saves run. Which one wins is decided
/// by the PostgreSQL row lock and by the <c>revoked_at</c> concurrency predicate EF puts in the UPDATE. No
/// sleep, delay, or stopwatch takes part, and the barrier never touches the seeding contexts.</para>
/// </remarks>
public sealed class RefreshCommandConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PresentedToken = "test-presented-refresh-token";

    private readonly SaveChangesBarrier barrier = new(participants: 2);

    [Fact]
    public async Task ExactlyOneOfTwoSimultaneousRefreshesSucceeds()
    {
        await SeedActiveTokenAsync();

        var results = await RaceAsync();

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.IsFailure);
    }

    [Fact]
    public async Task TheLoserGetsThePublicInvalidRefreshResult()
    {
        await SeedActiveTokenAsync();

        var results = await RaceAsync();

        var loser = results.Single(result => result.IsFailure);

        Assert.NotNull(loser.Error);

        // The same error an unknown or already-rotated token produces. Nothing provider-specific reaches the
        // caller: not a concurrency exception, not a PostgreSQL error code, not a constraint name.
        Assert.Equal("authentication.invalid_refresh_token", loser.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, loser.Error.Type);
        Assert.DoesNotContain("concurren", loser.Error.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRaceLeavesTheOriginalTokenRevokedAndExactlyOneReplacement()
    {
        var originalTokenHash = await SeedActiveTokenAsync();

        var results = await RaceAsync();

        await using var verifying = Host.CreateVerificationContext();
        var tokens = await verifying.RefreshTokens.ToListAsync();

        Assert.Equal(2, tokens.Count);

        var original = Assert.Single(tokens, token => token.TokenHash == originalTokenHash);
        Assert.NotNull(original.RevokedAt);

        var replacement = Assert.Single(tokens, token => token.TokenHash != originalTokenHash);
        Assert.Null(replacement.RevokedAt);

        // The replacement belongs to the winner, so the losing save inserted nothing that survived.
        var winner = results.Single(result => result.IsSuccess);
        Assert.Equal(Host.Tokens.HashRefreshToken(winner.Value.RefreshToken), replacement.TokenHash);
    }

    [Fact]
    public async Task TheRaceNeverPersistsARawTokenValue()
    {
        await SeedActiveTokenAsync();

        var results = await RaceAsync();

        var winner = results.Single(result => result.IsSuccess);

        await using var verifying = Host.CreateVerificationContext();
        var storedHashes = await verifying.RefreshTokens.Select(token => token.TokenHash).ToListAsync();

        Assert.DoesNotContain(PresentedToken, storedHashes);
        Assert.DoesNotContain(winner.Value.RefreshToken, storedHashes);
        Assert.DoesNotContain(winner.Value.AccessToken, storedHashes);
    }

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [barrier];
    }

    private async Task<IReadOnlyList<Result<RefreshResult>>> RaceAsync()
    {
        var command = new RefreshCommand(PresentedToken);

        var first = HandleAsync(command);
        var second = HandleAsync(command);

        return await Task.WhenAll(first, second);
    }

    private async Task<Result<RefreshResult>> HandleAsync(RefreshCommand command)
    {
        // A scope per competing request: two contexts, two units of work, nothing shared.
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<RefreshCommandHandler>().Handle(command);
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
