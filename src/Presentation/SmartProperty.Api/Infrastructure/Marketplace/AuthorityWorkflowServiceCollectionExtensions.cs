#nullable enable
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Security;
using SmartProperty.Api.Infrastructure.Storage;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Security;
using SmartProperty.Application.Abstractions.Storage;
using SmartProperty.Application.Identity.StaffRoles;
using SmartProperty.Application.Marketplace.AuthorityRequests;

namespace SmartProperty.Api.Infrastructure.Marketplace;

/// <summary>The "StaffRoles" section.</summary>
internal sealed class StaffRolesOptions
{
    public const string SectionName = "StaffRoles";

    /// <summary>Provision the Legal Reviewer role and the administrator's marketplace permissions at start. Default on.</summary>
    public bool ProvisionOnStartup { get; init; } = true;
}

/// <summary>Runs the staff role catalogue once at start (after the platform administrator bootstrap).</summary>
internal sealed class StaffRoleCatalogService(
    IOptions<StaffRolesOptions> options,
    IServiceScopeFactory scopes,
    ILogger<StaffRoleCatalogService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ProvisionOnStartup)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<MarketplaceStaffRoleCatalog>().EnsureAsync(cancellationToken);

        if (!result.MadeNoChange)
        {
            logger.LogWarning(
                "Staff role catalogue: {PermissionsCreated} permissions created, Legal Reviewer role created: {RoleCreated}, "
                + "{Attached} role permissions attached.",
                result.PermissionsCreated, result.LegalReviewerRoleCreated, result.RolePermissionsAttached);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class AuthorityWorkflowServiceCollectionExtensions
{
    /// <summary>
    /// The real advertising workflow: private document storage, the ClamAV scanner and its worker, document-backed
    /// evidence, recorded-relationship independence, and the staff roles. Nothing here simulates a check.
    /// </summary>
    public static IServiceCollection AddAuthorityWorkflow(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DocumentStorageOptions>()
            .Bind(configuration.GetSection(DocumentStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "DocumentStorage:RootPath must not be blank.")
            .Validate<IOptions<ImageStorageOptions>, IHostEnvironment>(
                (documents, images, environment) =>
                    !string.Equals(
                        LocalDiskImageStorage.ResolveRoot(documents.RootPath, environment),
                        LocalDiskImageStorage.ResolveRoot(images.Value.RootPath, environment),
                        StringComparison.OrdinalIgnoreCase),
                "DocumentStorage:RootPath must be a different directory from ImageStorage:RootPath.")
            .ValidateOnStart();
        services.AddSingleton<IPrivateDocumentStorage, LocalDiskPrivateDocumentStorage>();

        services.AddOptions<MalwareScanningOptions>()
            .Bind(configuration.GetSection(MalwareScanningOptions.SectionName))
            .Validate(options => options.IsValid(), "MalwareScanning settings are out of range.")
            .ValidateOnStart();
        services.TryAddSingleton<IMalwareScanner, ClamAvMalwareScanner>();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<MalwareScanningOptions>>().Value.ToPolicy());
        services.AddScoped<AuthorityDocumentScanProcessor>();
        services.AddHostedService<DocumentScanWorker>();
        services.AddHealthChecks().AddCheck<MalwareScannerHealthCheck>("malware-scanner", tags: ["scanner"]);

        // The production evidence and independence sources. Test hosts may replace them; demo mode (Development only)
        // replaces them with its simulations.
        services.AddScoped<AuthorityDocumentEvidenceProvider>();
        services.TryAddScoped<IEvidenceAssessmentProvider>(provider => provider.GetRequiredService<AuthorityDocumentEvidenceProvider>());
        services.TryAddScoped<IReviewerIndependenceProvider, RecordedRelationshipIndependenceProvider>();

        services.AddOptions<StaffRolesOptions>().Bind(configuration.GetSection(StaffRolesOptions.SectionName));
        services.AddScoped<MarketplaceStaffRoleCatalog>();
        services.AddScoped<LegalReviewerAdministration>();
        services.AddHostedService<StaffRoleCatalogService>();

        return services;
    }
}
