#nullable enable
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace SmartProperty.Api.Infrastructure.Localization;

/// <summary>Documents the optional <c>lang</c> query parameter on every operation, so API clients (Scalar) offer it.</summary>
public static class DisplayLanguageOpenApi
{
    public static void AddLanguageParameter(OpenApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        operation.Parameters ??= [];
        if (operation.Parameters.Any(parameter => parameter.Name == DisplayLanguage.QueryKey && parameter.In == ParameterLocation.Query))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = DisplayLanguage.QueryKey,
            In = ParameterLocation.Query,
            Required = false,
            Description = "Language of the display* fields and of displayMessage: ar or en. "
                + "Overrides the Accept-Language header; English when neither is given.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create(DisplayLanguage.Arabic), JsonValue.Create(DisplayLanguage.English)]
            }
        });
    }
}
