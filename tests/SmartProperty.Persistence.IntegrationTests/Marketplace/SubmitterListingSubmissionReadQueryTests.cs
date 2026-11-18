using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// These checks never connect. PostgreSQL execution and parameter inspection are in the companion fixture tests.
public sealed class SubmitterListingSubmissionReadQueryTests
{
    private static ServiceProvider Services() => new ServiceCollection()
        .AddPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"
        }).Build()).BuildServiceProvider();

    [Fact]
    public void RegistrationResolvesTheRealRepositoryWithScopedLifetime()
    {
        using var services = Services();
        using var first = services.CreateScope();
        using var second = services.CreateScope();
        var repository = first.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        Assert.Equal("SubmitterListingSubmissionReadRepository", repository.GetType().Name);
        Assert.Same(repository, first.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>());
        Assert.NotSame(repository, second.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<IReviewerListingSubmissionReadRepository>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<IOneTimeCodeRepository>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<IPropertyImageRepository>());
        Assert.Empty(first.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(PageParameters.MaxOffset + 1, 10)]
    [InlineData(int.MaxValue, 10)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(0, PageParameters.MaxPageSize + 1)]
    [InlineData(0, int.MaxValue)]
    public async Task InvalidPagingFailsBeforeConnecting(int skip, int take)
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListBySubmitterAsync(Guid.NewGuid(), null, null, skip, take));
    }

    [Fact]
    public async Task EmptyActorAndUndefinedFiltersFailBeforeConnecting()
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySubmitterAsync(Guid.Empty, null, null, 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetBySubmitterAsync(Guid.Empty, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySubmitterAsync(Guid.NewGuid(), (ListingPurpose)999, null, 0, 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySubmitterAsync(Guid.NewGuid(), null, (ListingSubmissionStatus)999, 0, 10));
        Assert.Null(await repository.GetBySubmitterAsync(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public async Task AlreadyCancelledReadsNeverConnect()
    {
        using var services = Services();
        using var scope = services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.ListBySubmitterAsync(Guid.NewGuid(), null, null, 0, 10, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetBySubmitterAsync(Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public void ScopedFiltersTranslateWithParametersWithoutMaterializingEntities()
    {
        using var services = Services();
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        var query = (IQueryable)repository.GetType().GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(repository, [Guid.NewGuid(), ListingPurpose.Rent, ListingSubmissionStatus.Draft])!;
        var sql = query.ToQueryString();
        Assert.Contains("ActingUserId", sql);
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.Contains("PurposeText", sql);
        Assert.Contains("StatusText", sql);
        Assert.Contains("@", sql);
        Assert.DoesNotContain("DeclaredOwner", sql);
        Assert.DoesNotContain("WorkspaceId", sql);
        Assert.DoesNotContain("review_decisions", sql);
        Assert.DoesNotContain("Description", sql);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }

    [Fact]
    public void ProjectionTypesContainExactlyThePermittedScalars()
    {
        var assembly = typeof(ApplicationDbContext).Assembly;
        var summary = assembly.GetType("SmartProperty.Persistence.Repositories.Marketplace.SubmitterListingSubmissionReadRow", true)!;
        var detail = assembly.GetType("SmartProperty.Persistence.Repositories.Marketplace.SubmitterListingSubmissionDetailReadRow", true)!;
        Assert.Equal(SummaryColumns.Order(), summary.GetProperties().Select(property => property.Name).Order());
        Assert.Equal(DetailColumns.Order(), detail.GetProperties().Select(property => property.Name).Order());
        Assert.Equal(typeof(DateTimeOffset?), summary.GetProperty("SubmittedAt")!.PropertyType);
        Assert.Equal(typeof(int?), summary.GetProperty("RentPeriod")!.PropertyType);
        Assert.Equal(typeof(bool?), detail.GetProperty("DecisionApproved")!.PropertyType);
        Assert.Equal(typeof(DateTimeOffset?), detail.GetProperty("DecidedAt")!.PropertyType);
    }

    internal static readonly string[] SummaryColumns =
    [
        "SubmissionId", "ListingId", "PurposeText", "Version", "StatusText", "Title", "Amount", "Currency",
        "RentPeriod", "Negotiable", "PropertyType", "Country", "Region", "City", "District", "Representation",
        "CreatedAt", "SubmittedAt", "UpdatedAt", "IsCurrentlyPublished", "PricingOptionsJson", "RentalTermsJson"
    ];

    internal static readonly string[] DetailColumns =
        [.. SummaryColumns, "Description", "DecisionApproved", "DecisionReason", "DecidedAt"];
}
