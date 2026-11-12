#nullable enable
using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Net.Http.Headers;

namespace SmartProperty.Api.Infrastructure.Localization;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>
    /// English and Arabic responses. The language comes from <c>?lang=ar|en</c>, then <c>Accept-Language</c>, and is
    /// English otherwise. Every JSON response then carries <c>display&lt;Field&gt;</c> values in that language
    /// (see <see cref="DisplayFieldsModifier"/>).
    /// </summary>
    public static IServiceCollection AddApiLocalization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<RequestLocalizationOptions>(options =>
        {
            var english = CultureInfo.GetCultureInfo(DisplayLanguage.English);

            options.DefaultRequestCulture = new RequestCulture(english, english);

            // Formatting and parsing always stay English, whatever is asked: only the display language changes.
            options.SupportedCultures = [english];
            options.SupportedUICultures = [.. DisplayLanguage.Supported.Select(CultureInfo.GetCultureInfo)];
            options.FallBackToParentCultures = true;
            options.FallBackToParentUICultures = true;
            options.ApplyCurrentCultureToResponseHeaders = false;
            options.RequestCultureProviders =
            [
                new QueryStringRequestCultureProvider
                {
                    QueryStringKey = DisplayLanguage.QueryKey,
                    UIQueryStringKey = DisplayLanguage.QueryKey
                },
                new AcceptLanguageHeaderRequestCultureProvider()
            ];
        });

        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
            DisplayFieldsModifier.Apply(options.JsonSerializerOptions));

        // Used by WriteAsJsonAsync (error bodies written outside MVC) and by the OpenAPI document.
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            DisplayFieldsModifier.Apply(options.SerializerOptions));

        return services;
    }

    /// <summary>
    /// Picks the request's language and states it in <c>Content-Language</c> (with <c>Vary: Accept-Language</c>).
    /// Must run before the exception handler so error bodies are translated too.
    /// </summary>
    public static IApplicationBuilder UseApiLocalization(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseRequestLocalization();

        return app.Use(async (context, next) =>
        {
            var language = DisplayLanguage.Current;
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers[HeaderNames.ContentLanguage] = language;
                headers.Append(HeaderNames.Vary, HeaderNames.AcceptLanguage);

                return Task.CompletedTask;
            });

            await next(context);
        });
    }
}
