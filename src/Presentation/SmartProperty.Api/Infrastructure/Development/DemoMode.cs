#nullable enable
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Controllers;
using SmartProperty.Application.Abstractions.Marketplace;

namespace SmartProperty.Api.Infrastructure.Development;

/// <summary>
/// Demo mode: stand-ins for the three external checks the marketplace cannot make yet (evidence scanning, reviewer
/// independence beyond what the database can show, and the legal-review provenance of a grant), so a whole walkthrough can
/// be exercised in a development environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two conditions, both required.</b> Nothing simulated is registered unless the host environment is
/// <c>Development</c> <b>and</b> <c>DemoMode:Enabled</c> is true. If the flag is true in any other environment (Staging,
/// Production, anything else) the host <b>refuses to start</b>: the options are validated on start, before the host
/// listens, so the process exits instead of quietly serving without the simulation or, worse, with it.
/// </para>
/// <para>
/// Demo mode replaces only the external checks. Ownership, grants, the lock order, state transitions, overlap protection
/// and the audit trail are the normal code.
/// </para>
/// </remarks>
public sealed class DemoModeOptions
{
    public const string SectionName = "DemoMode";

    public bool Enabled { get; set; }

    /// <summary>Where registered evidence fixtures are kept. Required when demo mode is on.</summary>
    public string EvidenceRootPath { get; set; } = string.Empty;

    /// <summary>The largest fixture accepted, in bytes.</summary>
    public long MaxEvidenceBytes { get; set; } = 5 * 1024 * 1024;
}

public sealed class DemoModeOptionsValidator(IHostEnvironment environment) : IValidateOptions<DemoModeOptions>
{
    public ValidateOptionsResult Validate(string? name, DemoModeOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (!environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"DemoMode:Enabled is true but the environment is '{environment.EnvironmentName}'. Simulated evidence and "
                + "reviewer checks are allowed only in Development, so the application refuses to start.");
        }

        if (string.IsNullOrWhiteSpace(options.EvidenceRootPath))
        {
            return ValidateOptionsResult.Fail("DemoMode:EvidenceRootPath is required when demo mode is enabled.");
        }

        if (options.MaxEvidenceBytes is < 1 or > 50 * 1024 * 1024)
        {
            return ValidateOptionsResult.Fail("DemoMode:MaxEvidenceBytes must be between 1 byte and 50 MB.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal static class DemoModeServiceCollectionExtensions
{
    /// <summary>The simulated providers and the fixture endpoint exist only when this answers true.</summary>
    public static bool IsActive(IServiceProvider provider)
    {
        return provider.GetRequiredService<IHostEnvironment>().IsDevelopment()
            && provider.GetRequiredService<IOptions<DemoModeOptions>>().Value.Enabled;
    }

    public static IServiceCollection AddDemoMode(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DemoModeOptions>()
            .BindConfiguration(DemoModeOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DemoModeOptions>, DemoModeOptionsValidator>());

        services.TryAddSingleton<EvidenceFixtureStore>();
        services.TryAddScoped<ReviewerRelationshipChecker>();

        // Decided when a provider is resolved, from the validated options, never from a flag read earlier.
        services.Replace(ServiceDescriptor.Scoped<IEvidenceAssessmentProvider>(provider => IsActive(provider)
            ? ActivatorUtilities.CreateInstance<SimulatedEvidenceAssessmentProvider>(provider)
            : provider.GetRequiredService<SmartProperty.Application.Marketplace.AuthorityRequests.AuthorityDocumentEvidenceProvider>()));
        services.Replace(ServiceDescriptor.Scoped<IReviewerIndependenceProvider>(provider => IsActive(provider)
            ? ActivatorUtilities.CreateInstance<RelationshipReviewerIndependenceProvider>(provider)
            : ActivatorUtilities.CreateInstance<SmartProperty.Api.Infrastructure.Marketplace.RecordedRelationshipIndependenceProvider>(provider)));

        // The fixture endpoint is not merely guarded: outside demo mode it is removed from the application model, so it
        // has no route and does not appear in the OpenAPI document.
        services.AddOptions<MvcOptions>().Configure<IOptions<DemoModeOptions>, IHostEnvironment>((mvc, demo, environment) =>
        {
            if (!(environment.IsDevelopment() && demo.Value.Enabled))
            {
                mvc.Conventions.Add(new RemoveDemoControllersConvention());
            }
        });

        return services;
    }

    private sealed class RemoveDemoControllersConvention : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            foreach (var controller in application.Controllers.Where(c => c.ControllerType == typeof(DevelopmentEvidenceController)).ToArray())
            {
                application.Controllers.Remove(controller);
            }
        }
    }
}
