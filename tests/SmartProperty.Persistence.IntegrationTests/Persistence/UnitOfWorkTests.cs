using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Exercises the real <c>UnitOfWork</c> as the single commit boundary: a change is in the database after it is
/// called, and is not in the database before.
/// </summary>
/// <remarks>
/// Every assertion is made from a context that did not make the change, so no result can be produced by the
/// writing context's own change tracker. The number <c>SaveChangesAsync</c> returns is never the proof.
/// </remarks>
public sealed class UnitOfWorkTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task AMutationOfATrackedEntityIsInTheDatabaseAfterSaveChanges()
    {
        var user = AccessGraph.PendingUser("activation@example.test");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            await seeding.SaveChangesAsync();
        }

        using (var mutating = Host.CreateScope())
        {
            var users = mutating.ServiceProvider.GetRequiredService<IUserRepository>();
            var unitOfWork = mutating.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var tracked = await users.GetByIdAsync(user.Id);
            Assert.NotNull(tracked);

            tracked.Activate(TestClock.DefaultNow.AddMinutes(1));

            await unitOfWork.SaveChangesAsync();
        }

        await using var verifying = Host.CreateVerificationContext();
        var persisted = await verifying.Users.SingleAsync(row => row.Id == user.Id);

        Assert.Equal(Domain.Identity.UserStatus.Active, persisted.Status);
        Assert.Equal(TestClock.DefaultNow.AddMinutes(1), persisted.UpdatedAt);
    }

    [Fact]
    public async Task ATrackedChangeIsNotInTheDatabaseUntilSaveChangesIsCalled()
    {
        var user = AccessGraph.ActiveUser("uncommitted@example.test");

        using (var writing = Host.CreateScope())
        {
            // The repository only tracks. Nothing commits it, and the scope ends discarding it.
            await writing.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
        }

        await using var verifying = Host.CreateVerificationContext();

        Assert.Empty(await verifying.Users.ToListAsync());
    }

    [Fact]
    public async Task SeveralRepositoriesInOneScopeCommitTogetherInASingleSave()
    {
        var user = AccessGraph.ActiveUser("atomic@example.test");

        using (var writing = Host.CreateScope())
        {
            await writing.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(user);
            await writing.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
                .AddAsync(AccessGraph.ActiveRefreshToken(user.Id, "test-token-hash-atomic"));

            var affected = await writing.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

            // Reported for completeness; the database state below is what the test actually rests on.
            Assert.Equal(2, affected);
        }

        await using var verifying = Host.CreateVerificationContext();

        Assert.Single(await verifying.Users.ToListAsync());
        Assert.Single(await verifying.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task ARejectedSaveCommitsNoneOfItsChanges()
    {
        // The refresh token is valid; the user duplicates an email that already exists. Both are in one save, so
        // the token must not survive the rejection either.
        const string Email = "atomic.rejected@example.test";

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(AccessGraph.ActiveUser(Email));
            await seeding.SaveChangesAsync();
        }

        using (var writing = Host.CreateScope())
        {
            var duplicate = AccessGraph.ActiveUser(Email);

            await writing.ServiceProvider.GetRequiredService<IUserRepository>().AddAsync(duplicate);
            await writing.ServiceProvider.GetRequiredService<IRefreshTokenRepository>()
                .AddAsync(AccessGraph.ActiveRefreshToken(duplicate.Id, "test-token-hash-rolled-back"));

            var unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => unitOfWork.SaveChangesAsync());
        }

        await using var verifying = Host.CreateVerificationContext();

        Assert.Single(await verifying.Users.ToListAsync());
        Assert.Empty(await verifying.RefreshTokens.ToListAsync());
    }
}
