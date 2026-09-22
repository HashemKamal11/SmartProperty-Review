using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the persistence-backed <see cref="IPermissionChecker"/> so authorization can be exercised
/// without a database, and records exactly what the bridge asked it.
/// </summary>
/// <remarks>
/// One instance belongs to one test's factory; nothing is shared or static, so tests cannot see each other's
/// recorded calls no matter what order or degree of parallelism the runner chooses.
/// </remarks>
internal sealed class FakePermissionChecker : IPermissionChecker
{
    private readonly List<AuthorizationRequest> _requests = [];

    /// <summary>What the checker answers. Ignored when <see cref="ExceptionToThrow"/> is set.</summary>
    public AuthorizationDecision Decision { get; set; } = AuthorizationDecision.Denied;

    /// <summary>
    /// Thrown instead of answering, to prove an infrastructure failure is not silently turned into a denial.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    public IReadOnlyList<AuthorizationRequest> Requests => _requests;

    public int CallCount => _requests.Count;

    public AuthorizationRequest? LastRequest => _requests.Count == 0 ? null : _requests[^1];

    public CancellationToken LastCancellationToken { get; private set; }

    /// <summary>Answers <see cref="AuthorizationDecision.Allowed"/> for the codes added here, otherwise denies.</summary>
    public HashSet<string> AllowedPermissionCodes { get; } = new(StringComparer.Ordinal);

    public Task<AuthorizationDecision> CheckAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        LastCancellationToken = cancellationToken;

        if (ExceptionToThrow is { } exception)
        {
            throw exception;
        }

        var decision = AllowedPermissionCodes.Count > 0
            ? AllowedDecisionFor(request.PermissionCode)
            : Decision;

        return Task.FromResult(decision);
    }

    private AuthorizationDecision AllowedDecisionFor(string permissionCode)
    {
        return AllowedPermissionCodes.Contains(permissionCode)
            ? AuthorizationDecision.Allowed
            : AuthorizationDecision.Denied;
    }
}
