using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartProperty.Persistence;

namespace SmartProperty.Migrator;

/// <summary>The Migrator executable's configuration, dependency-injection, execution, and exit-code boundary.</summary>
public static class MigratorApplication
{
    /// <summary>
    /// Runs the same in-process path used by <c>Program.cs</c>. The optional builder callback lets integration
    /// tests add isolated configuration after the standard JSON/environment/command-line providers without
    /// reproducing or altering production service registration.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        Action<ConfigurationManager>? configureConfiguration = null,
        TextWriter? errorWriter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        errorWriter ??= Console.Error;

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            configureConfiguration?.Invoke(builder.Configuration);

            builder.Services.AddPersistence(builder.Configuration);
            builder.Services.AddOptions<InitialWorkspaceOptions>()
                .Bind(builder.Configuration.GetSection(InitialWorkspaceOptions.SectionName));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<MigrationProvisioningRunner>();

            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();

            await scope.ServiceProvider
                .GetRequiredService<MigrationProvisioningRunner>()
                .RunAsync(cancellationToken);

            return 0;
        }
        catch (MigrationProvisioningException exception)
        {
            await errorWriter.WriteLineAsync($"Migration/provisioning failed: {exception.Message}");
            return 1;
        }
        catch (Exception)
        {
            // Provider diagnostics emitted before this point have sensitive-data logging disabled. Deliberately
            // avoid printing an exception or connection string so credentials cannot enter deployment logs.
            await errorWriter.WriteLineAsync(
                "Migration/provisioning failed unexpectedly. Verify database availability, configuration, and migrations.");
            return 1;
        }
    }
}
