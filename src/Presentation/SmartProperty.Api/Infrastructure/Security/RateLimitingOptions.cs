#nullable enable
namespace SmartProperty.Api.Infrastructure.Security;

/// <summary>
/// Per-client-address request limits for the endpoints that are open to anyone and that cost something: sign-in and
/// the code-sending account flows (guessing, email and SMS flooding) and the AI search (a paid upstream call).
/// Bound from the "RateLimiting" section; on by default.
/// </summary>
/// <remarks>
/// The client address is the connection's remote address. Behind a reverse proxy that is the proxy, so every client
/// would share one allowance: enable forwarded headers (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>) when, and
/// only when, the API is reachable solely through that proxy.
/// </remarks>
internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public const string AuthPolicyName = "auth";
    public const string SearchPolicyName = "search";

    public bool Enabled { get; init; } = true;

    /// <summary>Requests per client address per window to the sign-in and account-flow endpoints.</summary>
    public int AuthPermitLimit { get; init; } = 20;

    /// <summary>Requests per client address per window to the AI-backed search.</summary>
    public int SearchPermitLimit { get; init; } = 30;

    public int WindowSeconds { get; init; } = 60;

    public bool IsValid() => AuthPermitLimit > 0 && SearchPermitLimit > 0 && WindowSeconds is >= 1 and <= 3600;
}
