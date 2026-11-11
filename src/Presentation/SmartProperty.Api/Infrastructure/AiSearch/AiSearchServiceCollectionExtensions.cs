#nullable enable
using SmartProperty.Application.Abstractions.Ai;

namespace SmartProperty.Api.Infrastructure.AiSearch;

internal static class AiSearchServiceCollectionExtensions
{
    public static IServiceCollection AddAiSearch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiSearchOptions>()
            .Bind(configuration.GetSection(AiSearchOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{AiSearchOptions.SectionName} configuration is invalid. When Enabled, BaseUrl must be an absolute " +
                "http(s) URL and TimeoutSeconds must be between 1 and 300.")
            .ValidateOnStart();

        // The base address and timeout are applied when the client is created, from the validated options.
        services.AddHttpClient<IAiSearchGateway, AiSearchHttpGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiSearchOptions>>().Value;

            if (options.Enabled)
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            }
        });

        // Advertisements are indexed after the marketplace commits, best effort. The handlers depend on this interface
        // optionally, so a host without the AI service (or a test) simply does not index.
        services.AddScoped<IListingSearchIndexer, AiListingIndexer>();

        return services;
    }
}
