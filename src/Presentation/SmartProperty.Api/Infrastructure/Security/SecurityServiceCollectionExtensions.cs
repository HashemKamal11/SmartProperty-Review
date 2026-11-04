#nullable enable
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Errors;

namespace SmartProperty.Api.Infrastructure.Security;

internal static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{RateLimitingOptions.SectionName} configuration is invalid. Permit limits must be positive and " +
                "WindowSeconds must be between 1 and 3600.")
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(RateLimitingOptions.AuthPolicyName, context =>
                PartitionByClient(context, RateLimitingOptions.AuthPolicyName, options => options.AuthPermitLimit));

            limiter.AddPolicy(RateLimitingOptions.SearchPolicyName, context =>
                PartitionByClient(context, RateLimitingOptions.SearchPolicyName, options => options.SearchPermitLimit));

            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var context = rejected.HttpContext;

                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                await context.Response.WriteAsJsonAsync(
                    ApiErrorResponseFactory.TooManyRequests(context),
                    cancellationToken);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> PartitionByClient(
        HttpContext context,
        string policyName,
        Func<RateLimitingOptions, int> permitLimit)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        if (!options.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policyName}:{client}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit(options),
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }
}
