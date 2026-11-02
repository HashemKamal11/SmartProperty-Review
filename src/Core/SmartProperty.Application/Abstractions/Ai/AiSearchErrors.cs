using SmartProperty.Common.Results;

namespace SmartProperty.Application.Abstractions.Ai;

public static class AiSearchErrors
{
    /// <summary>The integration is switched off in configuration, so nothing was sent.</summary>
    public const string DisabledCode = "ai.disabled";

    /// <summary>The service could not be reached or refused the call for a reason that is not the caller's.</summary>
    public const string UnavailableCode = "ai.unavailable";

    public static Error Disabled { get; } = new(
        DisabledCode,
        "The AI search integration is disabled.",
        ErrorType.Failure);

    public static Error Unavailable(string detail)
    {
        return new Error(UnavailableCode, $"The AI search service is unavailable: {detail}", ErrorType.Failure);
    }

    /// <summary>The service understood the call and refused its content, so a retry will not help.</summary>
    public static Error Rejected(string detail)
    {
        return new Error("ai.rejected", $"The AI search service rejected the request: {detail}", ErrorType.Validation);
    }
}
