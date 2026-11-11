using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.Controllers;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Common.Pagination;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Submitter;

/// <summary>
/// The submitter reads as the production composition activates them: the controller is discovered, both handlers and
/// the Persistence repository are registered, and nothing that writes a listing submission is.
/// </summary>
/// <remarks>
/// Every test uses the untouched production composition (<c>mount: false</c>) with a connection string that points
/// nowhere. Only anonymous requests, or requests for which no route matches, are sent, so nothing reaches a handler, a
/// repository or a database.
/// </remarks>
public sealed class SubmitterReadActivationTests
{
    private const string ListRoute = "/api/marketplace/me/listing-submissions";
    private const string MarketplaceTypes = "SmartProperty.Application.Marketplace";

    private static readonly string[] SubmitterRoutes =
    [
        "api/marketplace/me/listing-submissions",
        "api/marketplace/me/listing-submissions/{submissionId:guid}",
    ];

    // ----- the controller itself

    [Fact]
    public void TheControllerIsPublicRequiresAuthenticationAndNoPermissionCode()
    {
        var type = typeof(SubmitterListingSubmissionsController);

        Assert.True(type.IsVisible);
        Assert.NotEmpty(type.GetCustomAttributes(typeof(IAuthorizeData), inherit: true));
        Assert.Empty(type.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        Assert.Empty(type.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true));
        Assert.Empty(type.GetCustomAttributes(typeof(RequireWorkspacePermissionAttribute), inherit: true));

        var actions = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { "GetById", "List" }, actions.Select(action => action.Name).Order(StringComparer.Ordinal).ToArray());

        foreach (var action in actions)
        {
            Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
            Assert.Empty(action.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true));
            Assert.Empty(action.GetCustomAttributes(typeof(RequireWorkspacePermissionAttribute), inherit: true));
            var methods = action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).SelectMany(attribute => attribute.HttpMethods).ToArray();
            Assert.Equal(new[] { "GET" }, methods);
        }
    }

    // ----- what the production host answers without any credential

    [Theory]
    [InlineData(ListRoute)]
    [InlineData(ListRoute + "/22222222-2222-2222-2222-222222222222")]
    public async Task AnAnonymousCallerIs401AndNothingIsRead(string route)
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(route);

        await AssertUnauthorizedAsync(response);
        Assert.Equal(0, host.Stub.ListCallCount);
        Assert.Equal(0, host.Stub.GetCallCount);
    }

    [Theory]
    [InlineData(ListRoute)]
    [InlineData(ListRoute + "/22222222-2222-2222-2222-222222222222")]
    public async Task AGarbageOrExpiredTokenIs401(string route)
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var garbage = host.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var expired = host.CreateClient();
        expired.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwt.CreateExpiredAccessToken(Guid.NewGuid()));

        using var garbageResponse = await garbage.GetAsync(route);
        using var expiredResponse = await expired.GetAsync(route);

        await AssertUnauthorizedAsync(garbageResponse);
        await AssertUnauthorizedAsync(expiredResponse);
    }

    [Fact]
    public async Task OnlyTheApprovedVerbsAreAcceptedOnBothRoutes()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var client = host.CreateClient();

        // The list route reads (GET) and creates (POST); it never replaces, patches or deletes. The item route reads
        // (GET), replaces (PUT) and discards (DELETE); it never creates or patches.
        var notRouted = new Dictionary<string, HttpMethod[]>
        {
            [ListRoute] = [HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete],
            [$"{ListRoute}/{Guid.NewGuid()}"] = [HttpMethod.Post, HttpMethod.Patch],
        };

        foreach (var (route, methods) in notRouted)
        {
            foreach (var method in methods)
            {
                using var response = await client.SendAsync(new HttpRequestMessage(method, route));

                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task ANonGuidIdMatchesNoRoute()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{ListRoute}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- the route inventory

    [Fact]
    public void TheMarketplaceRoutesAreExactlyTheApprovedInventory()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);

        Assert.Equal(MarketplaceRouteInventory.ApprovedOrdered, MarketplaceRouteInventory.Actual(host.Services));
    }

    // ----- the container

    [Fact]
    public void TheProductionContainerRegistersBothReadHandlersAndThePersistenceRepository()
    {
        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.IsType<GetMyListingSubmissionsQueryHandler>(
            provider.GetRequiredService<IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>>>());
        Assert.IsType<GetMyListingSubmissionByIdQueryHandler>(
            provider.GetRequiredService<IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail>>());

        // The repository is the Persistence one, not the stub.
        var repository = provider.GetRequiredService<ISubmitterListingSubmissionReadRepository>();
        Assert.IsNotType<StubSubmitterListingSubmissionReadRepository>(repository);
        Assert.Same(typeof(SmartProperty.Persistence.AssemblyMarker).Assembly, repository.GetType().Assembly);
    }

    // Every command in the Marketplace application namespace, found by reflection rather than by name, so a write that
    // is added later is covered too. The anchor proves the scan sees the four draft writes that exist today.
    [Fact]
    public void EveryMarketplaceCommandHasItsHandlerRegisteredAndTheTrustedSeamsFailClosed()
    {
        var commands = typeof(SmartProperty.Application.AssemblyMarker).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && (type.Namespace ?? string.Empty).StartsWith(MarketplaceTypes, StringComparison.Ordinal))
            .Select(type => (Type: type, Handler: HandlerTypeFor(type)))
            .Where(candidate => candidate.Handler is not null)
            .ToArray();

        Assert.Superset(
            new HashSet<string>
            {
                "CreateListingDraftCommand", "CreatePropertyListingDraftsCommand", "UpdateListingDraftCommand", "SubmitListingDraftCommand",
                "DiscardListingDraftCommand", "WithdrawListingCommand", "ApproveListingSubmissionCommand", "RejectListingSubmissionCommand",
                "CreatePurchaseRequestCommand", "CreateRentalRequestCommand", "WithdrawRequestCommand", "RejectRequestCommand",
                "AcceptRequestCommand", "ConfirmCommitmentStepCommand", "SetSigningWindowCommand",
            },
            commands.Select(command => command.Type.Name).ToHashSet());

        using var host = new SubmitterApiTestHost(new StubSubmitterListingSubmissionReadRepository(), mount: false);
        using var scope = host.Services.CreateScope();

        foreach (var (type, handler) in commands)
        {
            Assert.True(scope.ServiceProvider.GetService(handler!) is not null, $"{type.Name} must have a registered handler.");
        }

        // The two trusted sources have no production implementation that can answer: evidence verification and reviewer
        // independence are documented blockers, and the registered ones refuse. A fake that answers is for tests only.
        Assert.Equal(
            "UnavailableEvidenceAssessmentProvider",
            scope.ServiceProvider.GetRequiredService<SmartProperty.Application.Abstractions.Marketplace.IEvidenceAssessmentProvider>().GetType().Name);
        Assert.Equal(
            "UnavailableReviewerIndependenceProvider",
            scope.ServiceProvider.GetRequiredService<SmartProperty.Application.Abstractions.Marketplace.IReviewerIndependenceProvider>().GetType().Name);
        Assert.Equal(
            "PlatformReviewDecisionAuthorizer",
            scope.ServiceProvider.GetRequiredService<SmartProperty.Application.Abstractions.Authorization.IReviewDecisionAuthorizer>().GetType().Name);
    }

    // ----- helpers

    private static Type? HandlerTypeFor(Type command)
    {
        foreach (var contract in command.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>))
            {
                return typeof(ICommandHandler<,>).MakeGenericType(command, contract.GetGenericArguments()[0]);
            }

            if (contract == typeof(ICommand))
            {
                return typeof(ICommandHandler<>).MakeGenericType(command);
            }
        }

        return null;
    }

    private static void AssertNotResolvable(IServiceProvider provider, string fullName)
    {
        if (typeof(SmartProperty.Application.AssemblyMarker).Assembly.GetType(fullName, throwOnError: false) is { } type)
        {
            Assert.Null(provider.GetService(type));
        }
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(header => header.Scheme));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authentication.unauthorized", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
    }
}
