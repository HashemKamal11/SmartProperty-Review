using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SmartProperty.Domain.Favorites;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Domain.PropertyComparison;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Configurations.Marketplace;
using SmartProperty.Persistence.Entities.Marketplace;

namespace SmartProperty.Persistence.Context;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OneTimeCode> OneTimeCodes => Set<OneTimeCode>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceAccessRequest> WorkspaceAccessRequests => Set<WorkspaceAccessRequest>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserPlatformRole> UserPlatformRoles => Set<UserPlatformRole>();
    public DbSet<WorkspaceMembershipRole> WorkspaceMembershipRoles => Set<WorkspaceMembershipRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Amenity> Amenities => Set<Amenity>();
    public DbSet<PropertyView> PropertyViews => Set<PropertyView>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Compound> Compounds => Set<Compound>();
    public DbSet<FavoriteProperty> FavoriteProperties => Set<FavoriteProperty>();
    public DbSet<OwnerProfile> OwnerProfiles => Set<OwnerProfile>();
    public DbSet<PropertyComparisonItem> PropertyComparisonItems => Set<PropertyComparisonItem>();
    public DbSet<PropertyImage> PropertyImages => Set<PropertyImage>();
    public DbSet<PropertyMatch> PropertyMatches => Set<PropertyMatch>();
    public DbSet<ListingSubmission> ListingSubmissions => Set<ListingSubmission>();
    public DbSet<ListingReviewDecision> ListingReviewDecisions => Set<ListingReviewDecision>();
    public DbSet<PropertyListing> PropertyListings => Set<PropertyListing>();
    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();
    public DbSet<RentalRequest> RentalRequests => Set<RentalRequest>();
    public DbSet<PropertyMarketplaceReservation> PropertyMarketplaceReservations => Set<PropertyMarketplaceReservation>();
    public DbSet<PropertyCommitment> PropertyCommitments => Set<PropertyCommitment>();
    public DbSet<MarketplaceSettings> MarketplaceSettings => Set<MarketplaceSettings>();
    public DbSet<PropertyAuthorityGrant> PropertyAuthorityGrants => Set<PropertyAuthorityGrant>();
    public DbSet<AuthorityRequest> AuthorityRequests => Set<AuthorityRequest>();
    public DbSet<AuthorityRequestDocument> AuthorityRequestDocuments => Set<AuthorityRequestDocument>();
    public DbSet<AuthorityRequestDecision> AuthorityRequestDecisions => Set<AuthorityRequestDecision>();

    public override int SaveChanges() => SaveChanges(true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateMarketplaceHistory();
        SynchronizeMarketplaceForeignKeys();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateMarketplaceHistory();
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
                    ValidateImmutable(entry, nameof(request.Id), nameof(request.ListingId), nameof(request.PropertyId), nameof(request.BuyerUserId), nameof(request.SubmittedAt), nameof(request.SubmissionId), nameof(request.OfferedAmount), nameof(request.Currency), nameof(request.Message));
                    PlanValue(entry, PurchaseRequestConfiguration.PurposeProperty, ListingPurpose.Sale, assignments);
                    break;
                case RentalRequest request:
                    ValidateIds(request.Id, request.ListingId, request.PropertyId, request.RenterUserId);
                    if (!Enum.IsDefined(request.Status))
                        throw new InvalidOperationException("Invalid rental request status.");
                    ValidateImmutable(entry, nameof(request.Id), nameof(request.ListingId), nameof(request.PropertyId), nameof(request.RenterUserId), nameof(request.SubmittedAt), nameof(request.SubmissionId), nameof(request.PricingPeriod), nameof(request.Amount), nameof(request.Currency), nameof(request.StartDate), nameof(request.EndDate), nameof(request.CheckInTime), nameof(request.CheckOutTime), nameof(request.TimeZoneId), nameof(request.Message));
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

    private static bool IsNeverSubmitted(ListingSubmission submission) =>
        submission.Status == ListingSubmissionStatus.Draft && submission.SubmittedAt is null
        && submission.ReviewDecision is null && submission.SubmittedAssessments.Count == 0;

    // EF save-boundary protection only: direct SQL/bulk updates require later database enforcement.
    private void ValidateMarketplaceHistory()
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries().ToArray();
        foreach (var entry in entries)
        {
            if (entry.Entity is ListingDraftDiscardAudit audit)
            {
                if (entry.State == EntityState.Added)
                {
                    // An audit row is only ever written together with the removal of the exact draft it describes.
                    var paired = entries.Any(other => other.State == EntityState.Deleted
                        && other.Entity is ListingSubmission removed
                        && removed.Id == audit.SubmissionId
                        && removed.PropertyId == audit.PropertyId
                        && IsNeverSubmitted(removed));
                    if (!paired)
                        throw new InvalidOperationException("A discard audit is written only together with the removal of the exact never-submitted draft it describes.");
                }
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("Draft discard audit history is append-only.");
            }
            if (entry.Entity is ListingReviewDecision && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Historical review decisions are immutable.");
            if (entry.Entity is ListingSubmission)
            {
                if (entry.State == EntityState.Deleted)
                {
                    var removed = (ListingSubmission)entry.Entity;
                    if (!IsNeverSubmitted(removed)) throw new InvalidOperationException("Submission history cannot be deleted.");
                    var audited = entries.Any(other => other.State == EntityState.Added
                        && other.Entity is ListingDraftDiscardAudit written && written.SubmissionId == removed.Id);
                    if (!audited)
                        throw new InvalidOperationException("A draft can be deleted only together with its discard audit record.");
                }
                if (entry.State == EntityState.Modified)
                {
                    foreach (var name in new[] { "Id", "ListingId", "PropertyId", "Purpose", "Version", "PredecessorId", "CreatedAt", "Authority" })
                        if (entry.Property(name).IsModified) throw new InvalidOperationException("Submission snapshots are immutable.");
                    var wasDraft = Equals(entry.Property("Status").OriginalValue, ListingSubmissionStatus.Draft);
                    var isDraft = Equals(entry.Property("Status").CurrentValue, ListingSubmissionStatus.Draft);
                    var snapshotChanged = entry.Property("Content").IsModified || entry.Property("Evidence").IsModified;
                    if (snapshotChanged && (!wasDraft || !isDraft || entry.Property("SubmittedAt").OriginalValue is not null))
                        throw new InvalidOperationException("Submitted snapshots are immutable.");
                    if (snapshotChanged || entry.Property("Revision").IsModified)
                    {
                        var before = (int)entry.Property("Revision").OriginalValue!;
                        var after = (int)entry.Property("Revision").CurrentValue!;
                        if (!wasDraft || before < 1 || before == int.MaxValue || after != before + 1)
                            throw new InvalidOperationException("A draft write requires the next revision.");
                    }
                    if (!wasDraft && (entry.Property("SubmittedAssessments").IsModified || entry.Property("SubmittedAt").IsModified))
                        throw new InvalidOperationException("Submitted evidence history is immutable.");
                    if (wasDraft && (entry.Property("SubmittedAssessments").IsModified || entry.Property("SubmittedAt").IsModified)
                        && !Equals(entry.Property("Status").CurrentValue, ListingSubmissionStatus.PendingReview))
                        throw new InvalidOperationException("Assessment assignment requires the submission transition.");
                }
            }
            if (entry.Entity is AuthorityRequestDecision && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Authority request decisions are append-only.");
            if (entry.Entity is AuthorityRequestDocument)
            {
                if (entry.State == EntityState.Deleted)
                    throw new InvalidOperationException("Authority request document versions are history and cannot be deleted.");
                if (entry.State == EntityState.Modified)
                {
                    // What was uploaded never changes; only its scan outcome, its place (quarantine to clean) and whether
                    // it is still in force do.
                    foreach (var name in new[] { "Id", "RequestId", "DocumentId", "VersionNumber", "Type", "ContentType",
                                 "FileName", "SizeBytes", "Sha256", "UploadedByUserId", "UploadedAt" })
                        if (entry.Property(name).IsModified) throw new InvalidOperationException("An uploaded document version is immutable.");
                    if (entry.Property("SupersededAt").IsModified && entry.Property("SupersededAt").OriginalValue is not null)
                        throw new InvalidOperationException("A superseded version stays superseded.");
                    if (entry.Property("RemovedAt").IsModified && entry.Property("RemovedAt").OriginalValue is not null)
                        throw new InvalidOperationException("A removed document stays removed.");
                }
            }
            if (entry.Entity is PropertyAuthorityGrant)
            {
                if (entry.State == EntityState.Deleted)
                    throw new InvalidOperationException("Authority grant history cannot be deleted.");
                if (entry.State == EntityState.Modified)
                {
                    // Only the revocation (and the version it bumps) may change; the subject and provenance never do.
                    foreach (var name in new[] { "Id", "PropertyId", "Purpose", "GranteeUserId", "Authority", "Issuer", "IssuedByUserId",
                                 "LegalReviewReferenceId", "LegalReviewer", "LegalReviewedByUserId", "LegalReviewedAt", "IssuedAt" })
                        if (entry.Property(name).IsModified) throw new InvalidOperationException("Authority grants are immutable except for revocation.");
                    if (entry.Property("RevokedAt").OriginalValue is not null)
                        throw new InvalidOperationException("Revocation is permanent.");
                }
            }
            if (entry.Entity is PropertyCommitment)
            {
                if (entry.State == EntityState.Deleted)
                    throw new InvalidOperationException("Commitments are history and cannot be deleted.");
                if (entry.State == EntityState.Modified)
                {
                    // Only the progress of the commitment changes; what was agreed, and with whom, never does.
                    foreach (var name in new[] { "Id", "PropertyId", "Kind", "ListingId", "SubmissionId", "PurchaseRequestId", "RentalRequestId",
                                 "CustomerUserId", "OwnerUserId", "PricingPeriod", "Amount", "Currency", "StartDate", "EndDate",
                                 "CheckInTime", "CheckOutTime", "TimeZoneId", "AcceptedAt", "SigningDeadline" })
                        if (entry.Property(name).IsModified) throw new InvalidOperationException("The terms of a commitment cannot be changed.");
                }
            }
            if (entry.Entity is PropertyListing && entry.State == EntityState.Modified
                && Equals(entry.Property("ClosedByReservation").OriginalValue, true)
                && !Equals(entry.Property("ClosedByReservation").CurrentValue, true))
                throw new InvalidOperationException("Reservation closure is permanent.");
        }
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
