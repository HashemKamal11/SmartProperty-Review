using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Application.OwnerVerification.Review;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Owner verification against PostgreSQL: the repository, the constraints that keep one profile per user and per
/// national ID, the table's own checks, and that approving really grants the right to create properties, and only that.
/// </summary>
public sealed class OwnerProfilePersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset T0 = TestClock.DefaultNow;
    private static readonly DateOnly Today = DateOnly.FromDateTime(T0.UtcDateTime);

    // ---- storage

    [Fact]
    public async Task AProfileRoundTripsWithEveryStep()
    {
        var user = await SeedUserAsync();
        var profile = Submitted(user.Id, "1234567890");
        await using (var context = Host.CreateVerificationContext())
        {
            context.OwnerProfiles.Add(profile);
            await context.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();
        var stored = await repository.GetByUserIdAsync(user.Id);

        Assert.NotNull(stored);
        Assert.Equal(profile.Id, stored.Id);
        Assert.Equal(OwnerVerificationStatus.UnderReview, stored.Status);
        Assert.Equal("1234567890", stored.NationalId);
        Assert.Equal(new DateOnly(1990, 1, 1), stored.BirthDate);
        Assert.Equal("1234", stored.BuildingNumber);
        Assert.Equal("owner-profiles/k.png", stored.AddressProofKey);
        Assert.Equal(T0, stored.SubmittedAt);
    }

    [Fact]
    public async Task ANationalIdIsReportedAsUsedOnlyByAnotherUser()
    {
        var owner = await SeedUserAsync();
        var other = await SeedUserAsync();
        await AddAsync(Submitted(owner.Id, "1234567890"));

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();

        Assert.True(await repository.NationalIdUsedByAnotherAsync("SA", "1234567890", other.Id));
        Assert.False(await repository.NationalIdUsedByAnotherAsync("SA", "1234567890", owner.Id));
        Assert.False(await repository.NationalIdUsedByAnotherAsync("SA", "1999999999", other.Id));
        // The same digits from another country are another document.
        Assert.False(await repository.NationalIdUsedByAnotherAsync("EG", "1234567890", other.Id));
    }

    [Fact]
    public async Task EgyptianAndEmiratiIdsRoundTrip()
    {
        var egyptian = await SeedUserAsync();
        var emirati = await SeedUserAsync();
        // The helper's birth date is 1990-01-01, which the Egyptian ID must encode.
        var egyptianProfile = Submitted(egyptian.Id, "29001010101234", country: NationalIdRules.Egypt);
        var emiratiProfile = Submitted(emirati.Id, "784-1990-1234567-1", country: NationalIdRules.UnitedArabEmirates);
        await AddAsync(egyptianProfile);
        await AddAsync(emiratiProfile);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();
        var storedEgyptian = await repository.GetByIdAsync(egyptianProfile.Id);
        var storedEmirati = await repository.GetByIdAsync(emiratiProfile.Id);

        Assert.Equal(("EG", "29001010101234"), (storedEgyptian!.IdCountryCode, storedEgyptian.NationalId));
        Assert.Equal(("AE", "784199012345671"), (storedEmirati!.IdCountryCode, storedEmirati.NationalId));
    }

    // ---- the queue

    [Fact]
    public async Task TheQueueExcludesDraftsFiltersByStatusAndPagesStably()
    {
        var draftUser = await SeedUserAsync();
        await AddAsync(new OwnerProfile(Guid.NewGuid(), draftUser.Id, T0));

        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var user = await SeedUserAsync();
            var profile = Submitted(user.Id, $"10000000{index:00}", submittedAt: T0.AddMinutes(index));
            await AddAsync(profile);
            ids.Add(profile.Id);
        }

        var decidedUser = await SeedUserAsync();
        var decided = Submitted(decidedUser.Id, "2000000000", submittedAt: T0.AddMinutes(10));
        decided.Approve(Guid.NewGuid(), T0.AddMinutes(11));
        await AddAsync(decided, reviewerMustExist: true);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();

        var all = await repository.ListForReviewAsync(null, 0, 10);
        var pending = await repository.ListForReviewAsync(OwnerVerificationStatus.UnderReview, 0, 2);
        var pendingNext = await repository.ListForReviewAsync(OwnerVerificationStatus.UnderReview, 2, 2);
        var approved = await repository.ListForReviewAsync(OwnerVerificationStatus.Approved, 0, 10);

        Assert.Equal(4, all.TotalCount);
        Assert.DoesNotContain(all.Items, item => item.UserId == draftUser.Id);
        Assert.Equal(3, pending.TotalCount);
        // Waiting profiles come oldest first, and the pages do not overlap.
        Assert.Equal([ids[0], ids[1]], pending.Items.Select(item => item.ProfileId));
        Assert.Equal([ids[2]], pendingNext.Items.Select(item => item.ProfileId));
        Assert.Equal(decided.Id, Assert.Single(approved.Items).ProfileId);
        Assert.Equal("Test", approved.Items.Single().FirstName);
    }

    // ---- uniqueness

    [Fact]
    public async Task AUserCannotHaveTwoProfiles()
    {
        var user = await SeedUserAsync();
        await AddAsync(new OwnerProfile(Guid.NewGuid(), user.Id, T0));

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();
        await repository.AddAsync(new OwnerProfile(Guid.NewGuid(), user.Id, T0));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.OwnerProfileUser, exception.Constraint);
    }

    [Fact]
    public async Task TwoUsersCannotHoldTheSameNationalIdButManyMayHaveNone()
    {
        var first = await SeedUserAsync();
        var second = await SeedUserAsync();
        await AddAsync(Submitted(first.Id, "1234567890"));

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>();
        await repository.AddAsync(Submitted(second.Id, "1234567890"));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
        Assert.Equal(PersistenceConstraint.OwnerProfileNationalId, exception.Constraint);

        // Profiles that have not saved the ID step hold null and do not collide.
        var third = await SeedUserAsync();
        var fourth = await SeedUserAsync();
        await AddAsync(new OwnerProfile(Guid.NewGuid(), third.Id, T0));
        await AddAsync(new OwnerProfile(Guid.NewGuid(), fourth.Id, T0));
    }

    // ---- the table's own checks

    [Theory]
    [InlineData("national_id", "'123'")]
    [InlineData("national_id", "'12345abcde'")]
    [InlineData("building_number", "'12a'")]
    [InlineData("building_number", "''")]
    [InlineData("additional_number", "'5a'")]
    [InlineData("postal_code", "'1234'")]
    [InlineData("additional_number", "'12345678901'")]
    [InlineData("status", "'Pending'")]
    public async Task TheDatabaseRefusesMalformedValues(string column, string value)
    {
        var user = await SeedUserAsync();
        // Status is a column of every row, so it replaces the default instead of being added a second time.
        var sql = column == "status"
            ? $"INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at) "
                + $"VALUES ('{Guid.NewGuid()}', '{user.Id}', {value}, now(), now())"
            : $"INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at, {column}) "
                + $"VALUES ('{Guid.NewGuid()}', '{user.Id}', 'Draft', now(), now(), {value})";

        await AssertRefusedAsync(sql);
    }

    [Theory]
    [InlineData("NULL", "'1234567890'")] // an ID without its country
    [InlineData("'SA'", "NULL")] // a country without an ID
    [InlineData("'SA'", "'123456789012345'")]
    [InlineData("'EG'", "'1234567890'")]
    [InlineData("'EG'", "'19001010101234'")]
    [InlineData("'AE'", "'123199012345671'")]
    [InlineData("'US'", "'1234567890'")]
    public async Task TheDatabaseRefusesAnIdThatDoesNotFitItsCountry(string country, string nationalId)
    {
        var user = await SeedUserAsync();

        await AssertRefusedAsync(
            "INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at, id_country_code, national_id) "
            + $"VALUES ('{Guid.NewGuid()}', '{user.Id}', 'Draft', now(), now(), {country}, {nationalId})");
    }

    private async Task AssertRefusedAsync(string sql)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));

        // A value too long for its column is refused by the column before the check is reached; either way it is refused.
        Assert.Contains(
            exception.SqlState,
            new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.StringDataRightTruncation });
    }

    [Fact]
    public async Task ADecidedProfileMustNameItsReviewerAndARejectedOneItsReason()
    {
        var user = await SeedUserAsync();

        var approvedWithoutReviewer = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at) "
            + $"VALUES ('{Guid.NewGuid()}', '{user.Id}', 'Approved', now(), now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, approvedWithoutReviewer.SqlState);

        var reviewer = await SeedUserAsync();
        var rejectedWithoutReason = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO identity.owner_profiles (id, user_id, status, reviewed_at, reviewed_by_user_id, created_at, updated_at) "
            + $"VALUES ('{Guid.NewGuid()}', '{user.Id}', 'Rejected', now(), '{reviewer.Id}', now(), now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejectedWithoutReason.SqlState);
    }

    // ---- approval grants exactly the right to create properties

    [Fact]
    public async Task ApprovingGrantsPropertyCreateAndReadAndNothingElse()
    {
        var owner = await SeedUserAsync();
        var reviewer = await SeedUserAsync();
        var profile = Submitted(owner.Id, "1234567890");
        await AddAsync(profile);

        using (var scope = Host.CreateScope())
        {
            var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyCreate));

            var result = await Approve(scope, profile.Id, reviewer.Id);

            Assert.True(result.IsSuccess);
            Assert.Equal("Approved", result.Value.Status);
        }

        using (var scope = Host.CreateScope())
        {
            var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

            Assert.True(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyCreate));
            Assert.True(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyRead));
            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyUpdate));
            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyArchive));
            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.OwnerVerificationReview));
            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.MarketplaceSubmissionsReviewDecide));
            Assert.False(await IsAllowedAsync(checker, reviewer.Id, PermissionCodes.PropertyCreate));
        }
    }

    [Fact]
    public async Task ApprovingASecondOwnerReusesTheRoleAndStoresItOnce()
    {
        var reviewer = await SeedUserAsync();
        var profiles = new List<OwnerProfile>();

        foreach (var nationalId in new[] { "1111111111", "2222222222" })
        {
            var owner = await SeedUserAsync();
            var profile = Submitted(owner.Id, nationalId);
            await AddAsync(profile);
            profiles.Add(profile);
        }

        foreach (var profile in profiles)
        {
            using var scope = Host.CreateScope();
            Assert.True((await Approve(scope, profile.Id, reviewer.Id)).IsSuccess);
        }

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM identity.roles WHERE name = 'Property Owner';"));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM identity.permissions WHERE code IN ('property.create', 'property.read');"));
        Assert.Equal(2L, await ScalarAsync(
            "SELECT count(*) FROM identity.role_permissions rp JOIN identity.roles r ON r.id = rp.role_id WHERE r.name = 'Property Owner';"));
        Assert.Equal(2L, await ScalarAsync(
            "SELECT count(*) FROM identity.user_platform_roles upr JOIN identity.roles r ON r.id = upr.role_id WHERE r.name = 'Property Owner';"));
    }

    [Fact]
    public async Task EditingAnApprovedProfileTakesTheGrantAway()
    {
        var owner = await SeedUserAsync();
        var reviewer = await SeedUserAsync();
        var profile = Submitted(owner.Id, "1234567890");
        await AddAsync(profile);
        using (var scope = Host.CreateScope())
        {
            Assert.True((await Approve(scope, profile.Id, reviewer.Id)).IsSuccess);
        }

        using (var scope = Host.CreateScope())
        {
            var handler = new SmartProperty.Application.OwnerVerification.Save.SaveOwnerAddressCommandHandler(
                scope.ServiceProvider.GetRequiredService<IUserRepository>(),
                scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>(),
                Provisioner(scope),
                scope.ServiceProvider.GetRequiredService<IDateTimeProvider>(),
                scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

            var edit = await handler.Handle(new SmartProperty.Application.OwnerVerification.Save.SaveOwnerAddressCommand(
                owner.Id, "9999", "New street", "New district", "Riyadh", "54321", "1111", true));

            Assert.True(edit.IsSuccess);
            Assert.Equal("Draft", edit.Value.Status);
        }

        using (var scope = Host.CreateScope())
        {
            var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

            Assert.False(await IsAllowedAsync(checker, owner.Id, PermissionCodes.PropertyCreate));
        }

        // The role itself stays for the next owner; only this user's assignment is gone.
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM identity.roles WHERE name = 'Property Owner';"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM identity.user_platform_roles;"));
    }

    // ---- helpers

    private static OwnerProfile Submitted(
        Guid userId,
        string nationalId,
        DateTimeOffset? submittedAt = null,
        string country = NationalIdRules.SaudiArabia)
    {
        var at = submittedAt ?? T0;
        var profile = new OwnerProfile(Guid.NewGuid(), userId, T0);
        profile.SaveIdentity(country, nationalId, "Saudi", new DateOnly(1990, 1, 1), Today.AddYears(5), true, Today, T0);
        profile.SaveAddress("1234", "Al Olaya", "Al Malqa", "Riyadh", "12345", "5678", true, T0);
        profile.SetAddressProof("owner-profiles/k.png", "image/png", "card.png", T0);
        profile.Submit(at);

        return profile;
    }

    private async Task<User> SeedUserAsync()
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "User", T0);
        user.Activate(T0);

        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user;
    }

    private async Task AddAsync(OwnerProfile profile, bool reviewerMustExist = false)
    {
        await using var context = Host.CreateVerificationContext();

        if (reviewerMustExist && profile.ReviewedByUserId is { } reviewerId)
        {
            context.Users.Add(new User(reviewerId, $"{Guid.NewGuid():n}@example.com", "Rev", "Iewer", T0));
            await context.SaveChangesAsync();
        }

        context.OwnerProfiles.Add(profile);
        await context.SaveChangesAsync();
    }

    private static OwnerAccessProvisioner Provisioner(IServiceScope scope)
    {
        return new OwnerAccessProvisioner(
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            scope.ServiceProvider.GetRequiredService<IDateTimeProvider>());
    }

    private static Task<SmartProperty.Common.Results.Result<OwnerProfileDetails>> Approve(
        IServiceScope scope,
        Guid profileId,
        Guid reviewerId)
    {
        var handler = new ApproveOwnerProfileCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserRepository>(),
            scope.ServiceProvider.GetRequiredService<IOwnerProfileRepository>(),
            Provisioner(scope),
            scope.ServiceProvider.GetRequiredService<IDateTimeProvider>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

        return handler.Handle(new ApproveOwnerProfileCommand(profileId, reviewerId));
    }

    private static async Task<bool> IsAllowedAsync(IPermissionChecker checker, Guid userId, string code)
    {
        var decision = await checker.CheckAsync(
            AuthorizationRequest.For(userId, code, AuthorizationTarget.Platform));

        return decision == AuthorizationDecision.Allowed;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var context = Host.CreateVerificationContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var context = Host.CreateVerificationContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
