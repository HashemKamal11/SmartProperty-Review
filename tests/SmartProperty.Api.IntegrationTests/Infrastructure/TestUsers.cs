using SmartProperty.Domain.Identity;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

internal static class TestUsers
{
    /// <summary>
    /// Stores an active user and returns their id, for a test that creates properties: a property records the user who
    /// registered it and that column references the users table, so the caller of the request has to exist.
    /// </summary>
    public static Guid Seed(ApiPostgreSqlFixture fixture)
    {
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "Owner", DateTimeOffset.UtcNow);
        user.Activate(DateTimeOffset.UtcNow);

        using var context = fixture.CreateContext();
        context.Users.Add(user);
        context.SaveChanges();

        return user.Id;
    }
}
