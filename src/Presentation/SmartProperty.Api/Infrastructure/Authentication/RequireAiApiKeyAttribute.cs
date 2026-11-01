#nullable enable
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// Authenticates the external AI system by the shared key in the <c>X-Api-Key</c> header. Fails closed: a missing
/// configuration, a missing header, and a wrong key are all the same <c>401</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class RequireAiApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configured = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<AiIntegrationOptions>>().Value.ApiKey;

        var supplied = context.HttpContext.Request.Headers[AiIntegrationOptions.ApiKeyHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(configured) || !KeysMatch(configured, supplied))
        {
            context.Result = new UnauthorizedResult();
        }
    }

    private static bool KeysMatch(string configured, string supplied)
    {
        // Hash both sides so the comparison is fixed-time regardless of length.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
