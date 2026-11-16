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

    /// <summary>
    /// Marks the <c>display*</c> fields that <see cref="DisplayFieldsModifier"/> adds as <c>readOnly</c>. They are written
    /// in responses and never read from a request, so API clients (Scalar) leave them out of request bodies.
    /// </summary>
    public static void MarkDisplayFieldsReadOnly(OpenApiSchema schema, System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            // Added fields have no member behind them; a real field named display... keeps its schema.
            if (property.AttributeProvider is not null
                || !property.Name.StartsWith(DisplayFieldsModifier.Prefix, StringComparison.Ordinal)
                || !schema.Properties.TryGetValue(property.Name, out var propertySchema)
                || propertySchema is not OpenApiSchema inline)
            {
                continue;
            }

            inline.ReadOnly = true;
        }
    }
}
