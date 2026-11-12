using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Persistence.Authorization;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Health;
using SmartProperty.Persistence.Repositories.Favorites;
using SmartProperty.Persistence.Repositories.OwnerVerification;
using SmartProperty.Persistence.Repositories.PropertyComparison;
using SmartProperty.Persistence.Repositories.Identity;
using SmartProperty.Persistence.Repositories.PropertyImages;
using SmartProperty.Persistence.Repositories.Locations;
using SmartProperty.Persistence.Repositories.Marketplace;
using SmartProperty.Persistence.Repositories.Platform;
using SmartProperty.Persistence.Repositories.PropertyMatching;
using SmartProperty.Persistence.Repositories.PropertyRegistry;

namespace SmartProperty.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Database");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Database is required. Set the ConnectionStrings__Database environment variable before starting the API or EF tooling.");
        }

        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IPlatformAdminBootstrapCoordinator, PostgreSqlPlatformAdminBootstrapCoordinator>();
        services.AddScoped<IWorkspaceApprovalCoordinator, PostgreSqlWorkspaceApprovalCoordinator>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IOneTimeCodeRepository, OneTimeCodeRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IPropertyRepository, PropertyRepository>();
        services.AddScoped<IAmenityRepository, AmenityRepository>();
        services.AddScoped<IViewRepository, ViewRepository>();
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<IFavoritePropertyRepository, FavoritePropertyRepository>();
        services.AddScoped<IOwnerProfileRepository, OwnerProfileRepository>();
        services.AddScoped<IAuthorityRequestRepository, Repositories.Marketplace.AuthorityRequestRepository>();
        services.AddScoped<IPropertyComparisonRepository, PropertyComparisonRepository>();
        services.AddScoped<IPropertyImageRepository, PropertyImageRepository>();
        services.AddScoped<IPropertyMatchRepository, PropertyMatchRepository>();
        services.AddScoped<PublicListingReadRepository>();
        services.AddScoped<IPublicListingReadRepository>(provider => provider.GetRequiredService<PublicListingReadRepository>());
        services.AddScoped<IPropertyListingsLookup>(provider => provider.GetRequiredService<PublicListingReadRepository>());
        services.AddScoped<IReviewerListingSubmissionReadRepository, ReviewerListingSubmissionReadRepository>();
        services.AddScoped<ISubmitterListingSubmissionReadRepository, SubmitterListingSubmissionReadRepository>();
        // One instance serves both roles, so a discard's audit row joins the same unit of work as the draft it removes.
        services.AddScoped<ListingSubmissionWriteRepository>();
        services.AddScoped<IListingSubmissionWriteRepository>(provider => provider.GetRequiredService<ListingSubmissionWriteRepository>());
        services.AddScoped<IMarketplaceRequestWriteRepository>(provider => provider.GetRequiredService<ListingSubmissionWriteRepository>());
        services.AddScoped<IMarketplaceRequestReadRepository, MarketplaceRequestReadRepository>();
        services.AddScoped<IMarketplaceSettingsRepository, MarketplaceSettingsRepository>();
        services.AddScoped<IListingSubmissionAuditRecorder>(provider => provider.GetRequiredService<ListingSubmissionWriteRepository>());
        services.AddScoped<IWorkspaceAccessRequestRepository, WorkspaceAccessRequestRepository>();
        services.AddScoped<IWorkspaceMembershipRepository, WorkspaceMembershipRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IAccessContextReader, AccessContextReader>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
