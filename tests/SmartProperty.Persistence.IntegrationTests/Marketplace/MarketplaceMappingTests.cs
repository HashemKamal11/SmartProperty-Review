using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// No fixture, connection opening, schema creation or Docker: saves are intercepted before SQL.
public sealed class MarketplaceMappingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task EverySavePathInitializesForeignKeysBeforeSaving(int path)
    {
        var interceptor = new SuppressSave();
        await using var context = Create(interceptor);
        var graph = MarketplaceGraph.Create();
        graph.Track(context);
        switch (path)
        {
            case 0: context.SaveChanges(); break;
            case 1: context.SaveChanges(true); break;
            case 2: context.SaveChanges(false); break;
            case 3: await context.SaveChangesAsync(); break;
            case 4: await context.SaveChangesAsync(true); break;
            case 5: await context.SaveChangesAsync(false); break;
            default:
                var type = typeof(ApplicationDbContext).Assembly.GetType("SmartProperty.Persistence.Context.UnitOfWork", throwOnError: true)!;
                var unitOfWork = (IUnitOfWork)Activator.CreateInstance(type, context)!;
                await unitOfWork.SaveChangesAsync();
                break;
        }
        Assert.Equal(1, interceptor.Calls);
        Assert.Equal(ListingPurpose.Sale, context.Entry(graph.Purchase).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
        Assert.Equal(ListingPurpose.Rent, context.Entry(graph.Rental).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
        Assert.Equal(graph.Purchase.Id, context.Entry(graph.Reservation).Property<Guid?>("PurchaseWinnerId").CurrentValue);
        Assert.Null(context.Entry(graph.Reservation).Property<Guid?>("RentalWinnerId").CurrentValue);
    }

    [Fact]
    public void ModelUsesOrdinaryShadowForeignKeysAndRestrictiveDeletes()
    {
        using var context = Create(new SuppressSave());
        foreach (var type in new[] { typeof(PropertyListing), typeof(PurchaseRequest), typeof(RentalRequest), typeof(PropertyMarketplaceReservation) })
        {
            var entity = context.Model.FindEntityType(type)!;
            Assert.Equal("marketplace", entity.GetSchema());
            Assert.All(entity.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior));
        }
        var reservation = context.Model.FindEntityType(typeof(PropertyMarketplaceReservation))!;
        Assert.Equal(nameof(PropertyMarketplaceReservation.PropertyId), Assert.Single(reservation.FindPrimaryKey()!.Properties).Name);
        foreach (var name in new[] { "PurchaseWinnerId", "RentalWinnerId" })
        {
            var property = reservation.FindProperty(name)!;
            Assert.True(property.IsShadowProperty());
            Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
            Assert.Null(property.GetComputedColumnSql());
        }
        var listing = context.Model.FindEntityType(typeof(PropertyListing))!;
        Assert.Contains(listing.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(p => p.Name).SequenceEqual(new[] { "PropertyId", "Purpose" }));
        var purchase = context.Model.FindEntityType(typeof(PurchaseRequest))!;
        Assert.Contains(purchase.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(PropertyListing) && key.Properties.Count == 3);
    }

    [Fact]
    public async Task ContradictoryAddedShadowValueFailsBeforeAnyAssignmentOrSave()
    {
        var interceptor = new SuppressSave();
        await using var context = Create(interceptor);
        var first = MarketplaceGraph.Create();
        var second = MarketplaceGraph.Create();
        context.Add(first.Purchase);
        context.Add(second.Rental);
        context.Entry(second.Rental).Property<ListingPurpose?>("ListingPurpose").CurrentValue = ListingPurpose.Sale;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Null(context.Entry(first.Purchase).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
        Assert.Equal(0, interceptor.Calls);
    }

    [Fact]
    public async Task MissingPersistedShadowValueIsNeverRepaired()
    {
        await using var context = Create(new SuppressSave());
        var graph = MarketplaceGraph.Create();
        context.Attach(graph.Purchase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Null(context.Entry(graph.Purchase).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
    }

    [Fact]
    public async Task BothReservationWinnerBranchesCannotBePopulated()
    {
        await using var context = Create(new SuppressSave());
        var graph = MarketplaceGraph.Create();
        context.Add(graph.Reservation);
        context.Entry(graph.Reservation).Property<Guid?>("RentalWinnerId").CurrentValue = graph.Rental.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Null(context.Entry(graph.Reservation).Property<Guid?>("PurchaseWinnerId").CurrentValue);
    }

    [Fact]
    public async Task SynchronizationWorksWithAutomaticChangeDetectionDisabled()
    {
        await using var context = Create(new SuppressSave());
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var graph = MarketplaceGraph.Create(sale: false);
        graph.Track(context);
        await context.SaveChangesAsync();
        Assert.Equal(graph.Rental.Id, context.Entry(graph.Reservation).Property<Guid?>("RentalWinnerId").CurrentValue);
        Assert.Null(context.Entry(graph.Reservation).Property<Guid?>("PurchaseWinnerId").CurrentValue);
    }

    private static ApplicationDbContext Create(SuppressSave interceptor) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql()
            .AddInterceptors(interceptor).Options);

    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public int Calls { get; private set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Calls++;
            return InterceptionResult<int>.SuppressWithResult(0);
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }
}

internal sealed record MarketplaceGraph(Property Property, User User, PropertyListing Sale, PropertyListing Rent,
    PurchaseRequest Purchase, RentalRequest Rental, PropertyMarketplaceReservation Reservation)
{
    internal static readonly DateTimeOffset Time = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    internal static MarketplaceGraph Create(bool sale = true)
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("SA"), Time);
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.test", "Test", "User", Time);
        var saleListing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Sale, Time);
        var rentListing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Rent, Time);
        saleListing.Publish(Guid.NewGuid(), Time.AddSeconds(1));
        rentListing.Publish(Guid.NewGuid(), Time.AddSeconds(1));
        var purchase = new PurchaseRequest(Guid.NewGuid(), saleListing, user.Id, Time.AddSeconds(2));
        var rental = new RentalRequest(Guid.NewGuid(), rentListing, user.Id, Time.AddSeconds(2));
        var reservation = sale
            ? PropertyMarketplaceReservation.AcceptPurchase(saleListing, rentListing, purchase, [], [rental], Time.AddSeconds(3))
            : PropertyMarketplaceReservation.AcceptRental(rentListing, saleListing, rental, [], [purchase], Time.AddSeconds(3));
        return new(property, user, saleListing, rentListing, purchase, rental, reservation);
    }

    internal void Track(ApplicationDbContext context)
    {
        // Deliberately add dependents first: EF must order SQL from the completed FK graph.
        context.Add(Reservation);
        context.Add(Rental);
        context.Add(Purchase);
        context.Add(Rent);
        context.Add(Sale);
        context.Add(User);
        context.Add(Property);
    }
}
