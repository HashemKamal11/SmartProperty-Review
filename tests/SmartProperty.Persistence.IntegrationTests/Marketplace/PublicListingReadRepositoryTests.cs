using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Separately gated: this class starts the disposable PostgreSQL fixture.
public sealed class PublicListingReadRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private async Task<(PropertyListing Listing, ListingSubmission Submission)> Seed(ListingPurpose purpose, decimal amount = 100m)
    {
        await using var context = Host.CreateVerificationContext();
        var at = MarketplaceGraph.Time;
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), at);
        var listing = new PropertyListing(Guid.NewGuid(), property.Id, purpose, at);
        context.Add(property);
        context.Add(listing);
        await context.SaveChangesAsync();
        var template = MarketplaceGraph.Pending(listing, at.AddSeconds(1));
        var content = new ListingSubmissionContent("Legacy country", "Region", "City", "District", "Public title", "Public description",
            PropertyType.Unit, purpose, amount, "Legacy currency", purpose == ListingPurpose.Rent ? RentPeriod.Yearly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, template.Authority, template.Evidence, at.AddSeconds(1));
        submission.Submit(submission.Authority.ActingUserId, null, at.AddSeconds(1), MarketplaceGraph.Assessments(submission, at.AddSeconds(1)), true, null);
        context.Add(submission);
        await context.SaveChangesAsync();
        submission.ApproveAndPublish(Guid.NewGuid(), listing, null, MarketplaceGraph.Reviewer(submission), at.AddSeconds(2), true,
            MarketplaceGraph.Assessments(submission, at.AddSeconds(2)), true, null);
        await context.SaveChangesAsync();
        return (listing, submission);
    }

    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public async Task PublicSnapshotPreservesLegacyValues(ListingPurpose purpose)
    {
        var (listing, _) = await Seed(purpose);
        using var services = new ServiceCollection().AddPersistence(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Host.ConnectionString }).Build()).BuildServiceProvider();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        var result = await repository.GetByIdAsync(listing.Id);
        Assert.NotNull(result);
        Assert.Equal("Legacy country", result.Location.CountryCode);
        Assert.Equal("Legacy currency", result.Price.Currency);
        Assert.Equal(purpose == ListingPurpose.Rent ? RentPeriod.Yearly : (RentPeriod?)null, result.Price.RentPeriod);
        var page = await repository.SearchAsync(PublicListingReadQueryTests.Criteria(), 0, 10);
        Assert.Equal(1, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Empty((await repository.SearchAsync(PublicListingReadQueryTests.Criteria() with { CountryCode = "EG" }, 0, 10)).Items);
        Assert.Empty((await repository.SearchAsync(PublicListingReadQueryTests.Criteria() with { City = "city" }, 0, 10)).Items);
        Assert.Single((await repository.SearchAsync(PublicListingReadQueryTests.Criteria() with { City = " City " }, 0, 10)).Items);
    }

    [Fact]
    public async Task PendingRevisionDoesNotReplacePublishedContentAndWithdrawalHidesIt()
    {
        var (listing, previous) = await Seed(ListingPurpose.Sale);
        await using (var context = Host.CreateVerificationContext())
        {
            var revision = MarketplaceGraph.Pending(listing, MarketplaceGraph.Time.AddSeconds(3), previous);
            context.Add(revision);
            await context.SaveChangesAsync();
        }
        using var services = new ServiceCollection().AddPersistence(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = Host.ConnectionString }).Build()).BuildServiceProvider();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPublicListingReadRepository>();
        Assert.Equal("Public title", (await repository.GetByIdAsync(listing.Id))!.Title);
        await using (var context = Host.CreateVerificationContext())
        {
            var tracked = await context.PropertyListings.SingleAsync(x => x.Id == listing.Id);
            tracked.Withdraw(MarketplaceGraph.Time.AddSeconds(4));
            await context.SaveChangesAsync();
        }
        Assert.Null(await repository.GetByIdAsync(listing.Id));
        Assert.Equal(0, (await repository.SearchAsync(PublicListingReadQueryTests.Criteria(), 0, 10)).TotalCount);
    }
}
