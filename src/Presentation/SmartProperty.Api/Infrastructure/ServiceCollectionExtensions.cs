#nullable enable
using Microsoft.AspNetCore.Mvc;
using SmartProperty.Api.Infrastructure.Errors;

namespace SmartProperty.Api.Infrastructure;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiConventions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddExceptionHandler<ApiExceptionHandler>();

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = actionContext =>
            {
                var response = ApiErrorResponseFactory.MalformedRequest(actionContext.HttpContext);

                return new ObjectResult(response)
                {
                    StatusCode = response.Status
                };
            };
        });

        return services;
    }
}
