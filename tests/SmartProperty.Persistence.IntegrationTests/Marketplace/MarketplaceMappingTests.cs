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

    [Fact]
    public void HistoryCheckRequiresNullWithdrawalOnlyForUnpublishedListings()
    {
        using var context = Create(new SuppressSave());
        var model = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(context).Model;
        var listing = model.FindEntityType(typeof(PropertyListing))!;
        var check = Assert.Single(listing.GetCheckConstraints(), x => x.Name == "ck_marketplace_listings_history");
        // Inspect the actual model, not an independently implemented SQL evaluator.
        // PostgreSQL execution of TRUE/FALSE/UNKNOWN cases remains separately gated.
        const string expected = "updated_at >= created_at AND ((publication_status = 'Unpublished' AND published_submission_id IS NULL AND published_at IS NULL AND withdrawn_at IS NULL) OR (publication_status = 'Published' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND published_at >= created_at AND published_at <= updated_at AND (withdrawn_at IS NULL OR (withdrawn_at >= created_at AND withdrawn_at <= published_at))) OR (publication_status = 'Withdrawn' AND published_submission_id IS NOT NULL AND published_at IS NOT NULL AND withdrawn_at IS NOT NULL AND published_at >= created_at AND withdrawn_at >= published_at AND withdrawn_at <= updated_at))";
        Assert.Equal(expected, check.Sql);
        foreach (var name in new[] { nameof(PropertyListing.CreatedAt), nameof(PropertyListing.UpdatedAt), nameof(PropertyListing.PublicationStatus), nameof(PropertyListing.AvailabilityStatus) })
            Assert.False(listing.FindProperty(name)!.IsNullable);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("published")]
    [InlineData("revision")]
    [InlineData("withdrawn")]
    [InlineData("republished")]
    [InlineData("reserved")]
    [InlineData("opposite-closed")]
    [InlineData("unpublished-closed")]
    public void DomainLifecycleRetainsValidHistoryShapes(string scenario)
    {
        var at = MarketplaceGraph.Time;
        var listing = new PropertyListing(Guid.NewGuid(), Guid.NewGuid(), ListingPurpose.Sale, at);
        if (scenario is "reserved" or "opposite-closed")
        {
            var graph = MarketplaceGraph.Create();
            listing = scenario == "reserved" ? graph.Sale : graph.Rent;
            Assert.True(listing.ClosedByReservation);
            Assert.Equal(scenario == "reserved" ? ListingAvailabilityStatus.Reserved : ListingAvailabilityStatus.Closed, listing.AvailabilityStatus);
        }
        else if (scenario == "unpublished-closed")
        {
            var winner = new PropertyListing(Guid.NewGuid(), listing.PropertyId, ListingPurpose.Rent, at);
            MarketplaceGraph.Approve(winner, at.AddSeconds(1));
            var request = new RentalRequest(Guid.NewGuid(), winner, Guid.NewGuid(), at.AddSeconds(2));
            PropertyMarketplaceReservation.AcceptRental(winner, listing, request, [], [], at.AddSeconds(3));
            Assert.True(listing.ClosedByReservation);
            Assert.Equal(ListingAvailabilityStatus.Closed, listing.AvailabilityStatus);
        }
        else if (scenario != "new")
        {
            var first = MarketplaceGraph.Approve(listing, at.AddSeconds(1));
            if (scenario is "withdrawn" or "republished") listing.Withdraw(at.AddSeconds(2));
            if (scenario is "revision" or "republished")
            {
                var next = MarketplaceGraph.Approve(listing, at.AddSeconds(3), first);
                Assert.Equal(next.Id, listing.PublishedSubmissionId);
            }
            if (scenario == "republished") Assert.Equal(at.AddSeconds(2), listing.WithdrawnAt);
        }

        Assert.True(listing.UpdatedAt >= listing.CreatedAt);
        if (listing.PublicationStatus == ListingPublicationStatus.Unpublished)
        {
            Assert.Null(listing.PublishedSubmissionId);
            Assert.Null(listing.PublishedAt);
            Assert.Null(listing.WithdrawnAt);
        }
        else
        {
            Assert.NotNull(listing.PublishedSubmissionId);
            Assert.NotNull(listing.PublishedAt);
            Assert.True(listing.PublishedAt >= listing.CreatedAt);
            Assert.True(listing.PublishedAt <= listing.UpdatedAt);
            if (listing.PublicationStatus == ListingPublicationStatus.Published)
            {
                Assert.Equal(ListingAvailabilityStatus.Open, listing.AvailabilityStatus);
                if (listing.WithdrawnAt.HasValue)
                    Assert.True(listing.WithdrawnAt >= listing.CreatedAt && listing.WithdrawnAt <= listing.PublishedAt);
            }
            else
            {
                Assert.Equal(ListingPublicationStatus.Withdrawn, listing.PublicationStatus);
                Assert.NotNull(listing.WithdrawnAt);
                Assert.True(listing.WithdrawnAt >= listing.PublishedAt && listing.WithdrawnAt <= listing.UpdatedAt);
            }
        }
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
    internal ListingSubmission SaleSubmission { get; private init; } = null!;
    internal ListingSubmission RentSubmission { get; private init; } = null!;
    internal static readonly DateTimeOffset Time = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    internal static MarketplaceGraph Create(bool sale = true)
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("SA"), Time);
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.test", "Test", "User", Time);
        var saleListing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Sale, Time);
        var rentListing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Rent, Time);
        var saleSubmission = Approve(saleListing, Time.AddSeconds(1));
        var rentSubmission = Approve(rentListing, Time.AddSeconds(1));
        var purchase = new PurchaseRequest(Guid.NewGuid(), saleListing, user.Id, Time.AddSeconds(2));
        var rental = new RentalRequest(Guid.NewGuid(), rentListing, user.Id, Time.AddSeconds(2));
        var reservation = sale
            ? PropertyMarketplaceReservation.AcceptPurchase(saleListing, rentListing, purchase, [], [rental], Time.AddSeconds(3))
            : PropertyMarketplaceReservation.AcceptRental(rentListing, saleListing, rental, [], [purchase], Time.AddSeconds(3));
        return new(property, user, saleListing, rentListing, purchase, rental, reservation) { SaleSubmission = saleSubmission, RentSubmission = rentSubmission };
    }

    internal static ListingSubmission Pending(PropertyListing listing, DateTimeOffset at, ListingSubmission? predecessor = null)
    {
        var actor = Guid.NewGuid();
        var authority = new ListingLegalAuthority(listing.PropertyId, listing.Purpose,
            new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), actor), actor,
            ListingRepresentationKind.Owner, null, listing.CreatedAt, null);
        var evidence = new ListingEvidenceReference(Guid.NewGuid(), Guid.NewGuid(), listing.PropertyId, listing.Purpose, new string('A', 64));
        var content = new ListingSubmissionContent("Country", "Region", "City", "District", "Title", "Description",
            SmartProperty.Domain.PropertyRegistry.PropertyType.Unit, listing.Purpose, 100m, "USD",
            listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null);
        var submission = new ListingSubmission(Guid.NewGuid(), listing, content, authority, [evidence], at, predecessor);
        submission.Submit(actor, null, at, Assessments(submission, at), true, null);
        return submission;
    }

    internal static ListingReviewContext Reviewer(ListingSubmission submission) => new(Guid.NewGuid(),
        submission.Authority.ActingUserId, submission.Authority.DeclaredOwner, submission.Authority.WorkspaceId,
        true, true, submission.Authority.WorkspaceId.HasValue ? false : null, false);

    internal static ListingEvidenceAssessment[] Assessments(ListingSubmission submission, DateTimeOffset at) =>
        submission.Evidence.Select(e => new ListingEvidenceAssessment(Guid.NewGuid(), e, EvidenceScanStatus.Clean, at)).ToArray();

    internal static ListingSubmission Approve(PropertyListing listing, DateTimeOffset at, ListingSubmission? predecessor = null)
    {
        var submission = Pending(listing, at, predecessor);
        submission.ApproveAndPublish(Guid.NewGuid(), listing, predecessor, Reviewer(submission), at, true,
            Assessments(submission, at), true, null);
        return submission;
    }
    internal async Task SeedPrincipalsAsync(ApplicationDbContext context)
    {
        context.Add(Property);
        context.Add(User);
        context.Add(new PropertyListing(Sale.Id, Property.Id, ListingPurpose.Sale, Time));
        context.Add(new PropertyListing(Rent.Id, Property.Id, ListingPurpose.Rent, Time));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.Add(SaleSubmission);
        context.Add(RentSubmission);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.Attach(Property);
        context.Attach(User);
        context.Update(Sale);
        context.Update(Rent);
        // Concurrency predicates must describe the inserted unpublished principal rows.
        foreach (var listing in new[] { Sale, Rent })
        {
            context.Entry(listing).Property(x => x.PublicationStatus).OriginalValue = ListingPublicationStatus.Unpublished;
            context.Entry(listing).Property(x => x.AvailabilityStatus).OriginalValue = ListingAvailabilityStatus.Open;
            context.Entry(listing).Property(x => x.ClosedByReservation).OriginalValue = false;
        }
        context.Add(Purchase);
        context.Add(Rental);
        context.Add(Reservation);
        await context.SaveChangesAsync();
    }
    internal void Track(ApplicationDbContext context)
    {
        // Deliberately add dependents first: EF must order SQL from the completed FK graph.
        context.Add(SaleSubmission);
        context.Add(RentSubmission);
        context.Add(Reservation);
        context.Add(Rental);
        context.Add(Purchase);
        context.Add(Rent);
        context.Add(Sale);
        context.Add(User);
        context.Add(Property);
    }
}
