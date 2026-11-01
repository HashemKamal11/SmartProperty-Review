using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// No fixture or host. Translation inspection does not establish PostgreSQL execution correctness.
public sealed class PublicListingReadQueryTests
{
    internal static ServiceProvider Services() => new ServiceCollection()
        .AddPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Database"] = "Host=127.0.0.1;Database=not_used" }).Build())
        .BuildServiceProvider();

    internal static PublicListingSearchCriteria Criteria() => new(null, null, null, null, null, null,
        null, null, null, null, PublicListingSortOrder.Newest);

    [Fact]
    public void RegistrationIsRealAndScoped()
    {
        using var services = Services();
        using var a = services.CreateScope();
        using var b = services.CreateScope();
        var repository = a.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        Assert.Equal("PublicListingReadRepository", repository.GetType().Name);
        Assert.Same(repository, a.ServiceProvider.GetRequiredService<IPublicListingReadRepository>());
        Assert.NotSame(repository, b.ServiceProvider.GetRequiredService<IPublicListingReadRepository>());
    }

    [Theory]
    [InlineData(PublicListingSortOrder.Newest)]
    [InlineData(PublicListingSortOrder.PriceAsc)]
    [InlineData(PublicListingSortOrder.PriceDesc)]
    public void FiltersOrderingAndPagingTranslateWithoutConnecting(PublicListingSortOrder sort)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        var type = repository.GetType();
        var criteria = Criteria() with { Purpose = ListingPurpose.Rent, Currency = "EGP", RentPeriod = RentPeriod.Monthly,
            CountryCode = "EG", Region = " Region ", City = "City", District = "District", MinPrice = 2m, MaxPrice = 100m, Sort = sort };
        var filtered = (IQueryable)type.GetMethod("Filter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repository, [criteria])!;
        var ordered = (IQueryable)type.GetMethod("Order", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [filtered, sort])!;
        var skip = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Skip), [ordered.ElementType], ordered.Expression, System.Linq.Expressions.Expression.Constant(10));
        var take = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Take), [ordered.ElementType], skip, System.Linq.Expressions.Expression.Constant(5));
        var sql = ordered.Provider.CreateQuery(take).ToQueryString();
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("marketplace.property_reservations", sql);
        Assert.Contains("s.id = l.published_submission_id", sql);
        Assert.Contains("s.listing_id = l.id", sql);
        Assert.Contains("s.property_id = l.property_id", sql);
        Assert.Contains("s.purpose = l.purpose", sql);
        Assert.Contains("p.status = 'Active'", sql);
        Assert.Contains("s.status = 'Approved'", sql);
        Assert.Contains("d.approved", sql);
        Assert.Contains("@", sql);
        Assert.DoesNotContain("authority_snapshot", sql);
        Assert.DoesNotContain("evidence_snapshot", sql);
        Assert.DoesNotContain("reviewer_snapshot", sql);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    [InlineData(1000000, 10)]
    public async Task InvalidPagingIsRejectedBeforeDatabaseAccess(int skip, int take)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.SearchAsync(Criteria(), skip, take));
    }

    [Theory]
    [InlineData(null, "EGP", null)]
    [InlineData(ListingPurpose.Sale, null, null)]
    [InlineData(ListingPurpose.Rent, "EGP", null)]
    [InlineData(ListingPurpose.Sale, "EGP", RentPeriod.Monthly)]
    public async Task MixedPriceContextsAreRejectedBeforeDatabaseAccess(ListingPurpose? purpose, string? currency, RentPeriod? period)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SearchAsync(
            Criteria() with { Purpose = purpose, Currency = currency, RentPeriod = period, Sort = PublicListingSortOrder.PriceAsc }, 0, 10));
    }

    [Fact]
    public void SqlRowContainsOnlyExplicitPublicFields()
    {
        var type = typeof(ApplicationDbContext).Assembly.GetType("SmartProperty.Persistence.Repositories.Marketplace.PublicListingReadRow", true)!;
        var expected = new[] { "ListingId", "Purpose", "Title", "Description", "Amount", "Currency", "RentPeriod", "PropertyType", "Country", "Region", "City", "District", "PublishedAt" };
        Assert.Equal(expected.Order(), type.GetProperties().Select(x => x.Name).Order());
    }
}
