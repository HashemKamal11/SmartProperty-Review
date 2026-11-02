#nullable enable
using SmartProperty.Application.Abstractions.Storage;

namespace SmartProperty.Api.Infrastructure.Storage;

internal static class ImageStorageServiceCollectionExtensions
{
    public static IServiceCollection AddImageStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ImageStorageOptions>()
            .Bind(configuration.GetSection(ImageStorageOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{ImageStorageOptions.SectionName}:RootPath must not be blank.")
            .ValidateOnStart();

        // Stateless apart from the directory it points at, so one instance serves every request.
        services.AddSingleton<IImageStorage, LocalDiskImageStorage>();

        return services;
    }
}
