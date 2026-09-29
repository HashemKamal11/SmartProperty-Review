using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Health;

internal sealed class DatabaseHealthCheck(ApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Database is unavailable.");
            }

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);

            return pendingMigrations.Any()
                ? HealthCheckResult.Unhealthy("Database schema has pending migrations.")
                : HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            // Readiness fails closed. The exception is retained for health-check diagnostics, while ASP.NET's
            // default response writer exposes only the aggregate status and no connection details.
            return HealthCheckResult.Unhealthy(
                "Database readiness verification failed.",
                exception);
        }
    }
}
