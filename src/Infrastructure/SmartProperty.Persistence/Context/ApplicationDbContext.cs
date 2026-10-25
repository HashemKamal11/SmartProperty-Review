using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Configurations.Marketplace;

namespace SmartProperty.Persistence.Context;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceAccessRequest> WorkspaceAccessRequests => Set<WorkspaceAccessRequest>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserPlatformRole> UserPlatformRoles => Set<UserPlatformRole>();
    public DbSet<WorkspaceMembershipRole> WorkspaceMembershipRoles => Set<WorkspaceMembershipRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<PropertyListing> PropertyListings => Set<PropertyListing>();
    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();
    public DbSet<RentalRequest> RentalRequests => Set<RentalRequest>();
    public DbSet<PropertyMarketplaceReservation> PropertyMarketplaceReservations => Set<PropertyMarketplaceReservation>();

    public override int SaveChanges() => SaveChanges(true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SynchronizeMarketplaceForeignKeys();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SynchronizeMarketplaceForeignKeys();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Derived FK values must exist before EF orders insert commands. Validate the whole tracked
    // Marketplace snapshot before filling missing values on Added entries; never repair persisted state.
    private void SynchronizeMarketplaceForeignKeys()
    {
        var entries = ChangeTracker.Entries().Where(entry => entry.Entity is
            PurchaseRequest or RentalRequest or PropertyMarketplaceReservation).ToArray();
        if (entries.Length == 0)
            return;

        ChangeTracker.DetectChanges();
        var assignments = new List<Action>();
        foreach (var entry in entries)
        {
            switch (entry.Entity)
            {
                case PurchaseRequest request:
                    ValidateIds(request.Id, request.ListingId, request.PropertyId, request.BuyerUserId);
                    if (!Enum.IsDefined(request.Status))
                        throw new InvalidOperationException("Invalid purchase request status.");
                    ValidateImmutable(entry, nameof(request.Id), nameof(request.ListingId), nameof(request.PropertyId), nameof(request.BuyerUserId), nameof(request.SubmittedAt));
                    PlanValue(entry, PurchaseRequestConfiguration.PurposeProperty, ListingPurpose.Sale, assignments);
                    break;
                case RentalRequest request:
                    ValidateIds(request.Id, request.ListingId, request.PropertyId, request.RenterUserId);
                    if (!Enum.IsDefined(request.Status))
                        throw new InvalidOperationException("Invalid rental request status.");
                    ValidateImmutable(entry, nameof(request.Id), nameof(request.ListingId), nameof(request.PropertyId), nameof(request.RenterUserId), nameof(request.SubmittedAt));
                    PlanValue(entry, RentalRequestConfiguration.PurposeProperty, ListingPurpose.Rent, assignments);
                    break;
                case PropertyMarketplaceReservation reservation:
                    ValidateIds(reservation.PropertyId, reservation.WinningListingId, reservation.WinningRequestId);
                    if (!Enum.IsDefined(reservation.ReservedPurpose))
                        throw new InvalidOperationException("Invalid reservation purpose.");
                    ValidateImmutable(entry, nameof(reservation.PropertyId), nameof(reservation.ReservedPurpose), nameof(reservation.WinningListingId), nameof(reservation.WinningRequestId), nameof(reservation.ReservedAt));
                    PlanValue(entry, PropertyMarketplaceReservationConfiguration.PurchaseWinnerProperty,
                        reservation.ReservedPurpose == ListingPurpose.Sale ? reservation.WinningRequestId : null, assignments);
                    PlanValue(entry, PropertyMarketplaceReservationConfiguration.RentalWinnerProperty,
                        reservation.ReservedPurpose == ListingPurpose.Rent ? reservation.WinningRequestId : null, assignments);
                    break;
            }
        }
        foreach (var assign in assignments)
            assign();
        ChangeTracker.DetectChanges();
    }

    private static void ValidateIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
            throw new InvalidOperationException("Marketplace identities must be nonempty.");
    }

    private static void ValidateImmutable(EntityEntry entry, params string[] names)
    {
        if (entry.State == EntityState.Added)
            return;
        foreach (var name in names)
        {
            var property = entry.Property(name);
            if (!Equals(property.CurrentValue, property.OriginalValue))
                throw new InvalidOperationException("Marketplace identity and creation facts cannot be changed.");
        }
    }

    private static void PlanValue(EntityEntry entry, string name, object? expected, List<Action> assignments)
    {
        var property = entry.Property(name);
        if (entry.State != EntityState.Added && !Equals(property.OriginalValue, expected))
            throw new InvalidOperationException("Persisted Marketplace foreign keys are inconsistent.");
        if (Equals(property.CurrentValue, expected))
            return;
        if (entry.State == EntityState.Added && property.CurrentValue is null && expected is not null)
            assignments.Add(() => property.CurrentValue = expected);
        else
            throw new InvalidOperationException("Marketplace foreign keys contradict the declared identity or purpose.");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
