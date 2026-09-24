using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// The permission bridge over real HTTP: the standardized 401 and 403 responses, what the checker is actually
/// asked, and what the public body is allowed to contain.
/// </summary>
/// <remarks>
/// xUnit constructs a new instance per test method, so each test gets its own host and its own
/// <see cref="FakePermissionChecker"/>. No state crosses tests.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PermissionEndpointTests : IDisposable
{
    private const string Endpoint = "/__tests__/authorization/platform";

    private readonly SmartPropertyApiFactory _factory;

    public PermissionEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _factory = new SmartPropertyApiFactory(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task AnonymousRequest_Returns401WithTheStandardUnauthorizedContract()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Unauthorized, error.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, error.Status);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
    }

    [Fact]
    public async Task AnonymousRequest_ChallengesWithTheBareBearerScheme()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, challenge.Scheme);

        // No error or error_description: which rule rejected the credential is not public information.
        Assert.Null(challenge.Parameter);
    }

    [Fact]
    public async Task AnonymousRequest_NeverReachesThePermissionChecker()
    {
        using var client = _factory.CreateClient();

        await client.GetAsync(Endpoint);

        Assert.Equal(0, _factory.PermissionChecker.CallCount);
    }

    [Fact]
    public async Task DeniedRequest_Returns403WithTheStandardForbiddenContract()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var error = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, error.Status);
        Assert.Null(error.FieldErrors);
        Assert.False(string.IsNullOrWhiteSpace(error.CorrelationId));
    }

    [Fact]
    public async Task DeniedRequest_AsksThePermissionCheckerExactlyOnce()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        await client.GetAsync(Endpoint);

        Assert.Equal(1, _factory.PermissionChecker.CallCount);
    }

    [Fact]
    public async Task AllowedRequest_Returns200()
    {
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestPermissionResponse>();
        Assert.True(body!.Allowed);
    }

    [Fact]
    public async Task AllowedRequest_AsksAboutTheTokenSubjectTheRequirementCodeAndThePlatformTarget()
    {
        var userId = Guid.NewGuid();
        _factory.PermissionChecker.Decision = AuthorizationDecision.Allowed;
        using var client = _factory.CreateAuthenticatedClient(userId);

        await client.GetAsync($"{Endpoint}?permission={TestPermissionController.DefaultPermissionCode}");

        var request = Assert.Single(_factory.PermissionChecker.Requests);
        Assert.Equal(userId, request.UserId);
        Assert.Equal(TestPermissionController.DefaultPermissionCode, request.PermissionCode);
        Assert.Same(AuthorizationTarget.Platform, request.Target);
        Assert.Equal(RoleScope.Platform, request.Target.Scope);
        Assert.Null(request.Target.WorkspaceId);
    }

    [Fact]
    public async Task UnauthorizedResponse_EchoesTheRequestCorrelationId()
    {
        const string correlationId = "tests-auth-401";
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add(CorrelationIdFeature.HeaderName, correlationId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdFeature.HeaderName)));
        Assert.Equal(correlationId, (await ReadErrorAsync(response)).CorrelationId);
    }

    [Fact]
    public async Task ForbiddenResponse_EchoesTheRequestCorrelationId()
    {
        const string correlationId = "tests-authz-403";
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Add(CorrelationIdFeature.HeaderName, correlationId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(CorrelationIdFeature.HeaderName)));
        Assert.Equal(correlationId, (await ReadErrorAsync(response)).CorrelationId);
    }

    [Fact]
    public async Task ForbiddenResponse_LeaksNeitherThePermissionNorTheAccessModel()
    {
        const string deniedPermission = "finance.payments.delete";
        _factory.PermissionChecker.Decision = AuthorizationDecision.Denied;
        using var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync($"{Endpoint}?permission={deniedPermission}");
        var body = await response.Content.ReadAsStringAsync();

        // The checker really was asked about that permission, so the absence below is about the response.
        Assert.Equal(deniedPermission, _factory.PermissionChecker.LastRequest!.PermissionCode);

        string[] mustNotAppear =
        [
            deniedPermission,
            "finance",
            "payments",
            typeof(SmartProperty.Api.Infrastructure.Authorization.PermissionRequirement).Name,
            typeof(SmartProperty.Api.Infrastructure.Authorization.PermissionAuthorizationHandler).Name,
            typeof(SmartProperty.Application.Abstractions.Authorization.IPermissionChecker).Name,
            "role",
            "workspace"
        ];

        foreach (var fragment in mustNotAppear)
        {
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }

        // Asserted on the deserialized contract too, so the result does not depend on JSON formatting.
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(ApiErrorCodes.Forbidden, error.Code);
        Assert.DoesNotContain(deniedPermission, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
    }

}
