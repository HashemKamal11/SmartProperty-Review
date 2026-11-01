#nullable enable
namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// Settings for the external AI matching system, bound from the "AiIntegration" section. The ApiKey is a secret:
/// supply it through environment variables (AiIntegration__ApiKey), never source control. Left blank, the
/// AI-facing endpoint refuses every request.
/// </summary>
internal sealed class AiIntegrationOptions
{
    public const string SectionName = "AiIntegration";
    public const string ApiKeyHeaderName = "X-Api-Key";

    public string ApiKey { get; init; } = string.Empty;
}
