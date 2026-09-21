#nullable enable
using Microsoft.AspNetCore.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.Infrastructure.Authorization;

/// <summary>
/// An authorization requirement naming one permission code: <i>this operation requires permission X</i>.
/// </summary>
/// <remarks>
/// It carries the permission and nothing else. What the permission is being exercised <i>against</i> is the
/// authorization resource — an <see cref="AuthorizationTarget"/> — so platform and workspace scope are not
/// duplicated here as a flag and a nullable id that could disagree with the target.
///
/// The code is developer-supplied server configuration, not user input: a blank one is a programming error and
/// throws rather than producing any HTTP response.
/// </remarks>
internal sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <exception cref="ArgumentException">The permission code is null, empty, or whitespace.</exception>
    public PermissionRequirement(string permissionCode)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            throw new ArgumentException("Permission code must not be empty.", nameof(permissionCode));
        }

        // Trimmed to match how Permission stores its code and how AuthorizationRequest normalizes one.
        // Case is left alone: permission codes are compared exactly (see docs/authorization-model.md).
        PermissionCode = permissionCode.Trim();
    }

    /// <summary>The stable machine-readable <c>Permission.Code</c>, never a role or policy name.</summary>
    public string PermissionCode { get; }
}
