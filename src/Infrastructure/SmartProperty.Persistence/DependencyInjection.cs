using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Health;
using SmartProperty.Persistence.Repositories.Identity;
using SmartProperty.Persistence.Repositories.Platform;

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
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IWorkspaceAccessRequestRepository, WorkspaceAccessRequestRepository>();
        services.AddScoped<IWorkspaceMembershipRepository, WorkspaceMembershipRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        return services;
    }
}
