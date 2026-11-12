#nullable enable
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Api.Infrastructure.Authorization;
using SmartProperty.Api.Infrastructure.Bootstrap;
using SmartProperty.Api.Infrastructure.Cors;
using SmartProperty.Api.Infrastructure.Sharing;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Time;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Login;
using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Authentication.Context;
using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Application.Authentication.EmailVerifications;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.PasswordReset;
using SmartProperty.Application.Authentication.PhoneVerifications;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Application.Abstractions.Notifications;
using SmartProperty.Api.Infrastructure.Notifications;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Application.PropertyMatching.Record;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Application.Marketplace.PublicListings.GetById;
using SmartProperty.Application.Marketplace.PublicListings.Search;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.Archive;
using SmartProperty.Application.PropertyRegistry.Share;
using SmartProperty.Application.PropertyRegistry.Amenities;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Application.PropertyImages.Delete;
using SmartProperty.Application.PropertyImages.GetFile;
using SmartProperty.Application.PropertyImages.Reorder;
using SmartProperty.Application.PropertyImages.Upload;
using SmartProperty.Application.Favorites;
using SmartProperty.Application.Favorites.Add;
using SmartProperty.Application.Favorites.List;
using SmartProperty.Application.Favorites.Remove;
using SmartProperty.Application.OwnerVerification;
using SmartProperty.Application.OwnerVerification.Get;
using SmartProperty.Application.OwnerVerification.Review;
using SmartProperty.Application.OwnerVerification.Save;
using SmartProperty.Application.OwnerVerification.Submit;
using SmartProperty.Application.PropertyComparison;
using SmartProperty.Application.PropertyComparison.Add;
using SmartProperty.Application.PropertyComparison.Get;
using SmartProperty.Application.PropertyComparison.Remove;
using SmartProperty.Domain.OwnerVerification;
using SmartProperty.Application.PropertyRegistry.Views;
using SmartProperty.Application.PropertyMatching.Search;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.PropertyRegistry.Create;
using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Application.PropertyRegistry.Update;
using SmartProperty.Application.WorkspaceAccessRequests.Approve;
using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Application.WorkspaceAccessRequests.Reject;
using SmartProperty.Application.Workspaces.Get;
using SmartProperty.Application.Workspaces.Administration;
using SmartProperty.Application.Workspaces.Administration.Create;
using SmartProperty.Application.Workspaces.Members.Create;
using SmartProperty.Application.Workspaces.Administration.Get;
using SmartProperty.Application.Workspaces.Administration.List;
using SmartProperty.Application.Workspaces.RegistrationOptions;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Api.Infrastructure;

internal static class ServiceCollectionExtensions
{
    internal const string FrontendCorsPolicyName = "Frontend";

    // Tolerance for clock drift between token issuer and validator.
    private static readonly TimeSpan AccessTokenClockSkew = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddApiCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FrontendCorsOptions>()
            .Bind(configuration.GetSection(FrontendCorsOptions.SectionName))
            .Validate(
                options => options.HasOnlyValidOrigins(),
                $"{FrontendCorsOptions.SectionName}:AllowedOrigins must contain only exact http:// or https:// " +
                "origins without wildcards, credentials, paths, queries, fragments, or a trailing slash.")
            .ValidateOnStart();

        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<FrontendCorsOptions>>((corsOptions, configuredOptions) =>
            {
                var allowedOrigins = configuredOptions.Value.GetNormalizedAllowedOrigins();

                corsOptions.AddPolicy(FrontendCorsPolicyName, policy =>
                {
                    if (allowedOrigins.Length > 0)
                    {
                        policy.WithOrigins(allowedOrigins);
                    }

                    policy
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

        return services;
    }

    public static IServiceCollection AddApiConventions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddExceptionHandler<ApiExceptionHandler>();

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = actionContext =>
            {
                var response = ApiErrorResponseFactory.MalformedRequest(actionContext.HttpContext);

                return new ObjectResult(response)
                {
                    StatusCode = response.Status
                };
            };
        });

        return services;
    }

    public static IServiceCollection AddDateTimeProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();

        return services;
    }

    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiIntegrationOptions>()
            .Bind(configuration.GetSection(AiIntegrationOptions.SectionName));

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{JwtOptions.SectionName} configuration is invalid. Issuer, Audience, positive token lifetimes, " +
                $"and a SigningKey of at least {JwtOptions.MinimumSigningKeyBytes} bytes are required. " +
                "Supply the signing key through User Secrets or the Jwt__SigningKey environment variable.")
            .Validate<IDateTimeProvider>(
                (options, dateTimeProvider) => options.HasRepresentableLifetimes(dateTimeProvider.UtcNow),
                $"{JwtOptions.SectionName} configuration is invalid. AccessTokenLifetime and RefreshTokenLifetime " +
                "must produce a representable expiration date when added to the current UTC time.")
            .ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ILoginAttemptLimiter, InMemoryLoginAttemptLimiter>();
        services.AddOptions<ContactVerificationOptions>().BindConfiguration(ContactVerificationOptions.SectionName);
        services.AddSingleton<IContactVerificationPolicy, ConfiguredContactVerificationPolicy>();
        services.AddSingleton<ISmsSender, LoggingSmsSender>();

        // Real SMTP when Smtp:Host is set, otherwise the logging stand-in, so a developer can run without a mail server.
        services.AddOptions<SmtpOptions>()
            .BindConfiguration(SmtpOptions.SectionName)
            .Validate(
                smtp => !smtp.IsConfigured || !string.IsNullOrWhiteSpace(smtp.FromAddress),
                "Smtp:FromAddress is required when Smtp:Host is set.")
            .ValidateOnStart();
        services.AddSingleton<IEmailSender>(provider =>
            provider.GetRequiredService<IOptions<SmtpOptions>>().Value.IsConfigured
                ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(provider)
                : ActivatorUtilities.CreateInstance<LoggingEmailSender>(provider));
        services.AddSingleton<ITokenProvider, TokenProvider>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptionsAccessor) =>
            {
                var jwtOptions = jwtOptionsAccessor.Value;

                // Keep JWT claim names as issued so "sub" is read consistently by CurrentUser.
                bearerOptions.MapInboundClaims = false;

                // Suppresses the framework's "error_description" detail in the WWW-Authenticate header, which
                // would otherwise say whether a token was expired, badly signed, or issued for another audience.
                bearerOptions.IncludeErrorDetails = false;

                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = TokenProvider.CreateSigningKey(jwtOptions.SigningKey),
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    ClockSkew = AccessTokenClockSkew
                };

                bearerOptions.Events = new JwtBearerEvents
                {
                    // Runs after signature, algorithm, issuer, audience, and lifetime validation succeed.
                    // A token without exactly one non-empty Guid "sub" never produces an authenticated principal.
                    OnTokenValidated = context =>
                    {
                        // The signed payload and the principal must both carry the same valid subject.
                        if (!AccessTokenSubject.TryGetPayloadUserId(context.SecurityToken, out var payloadUserId)
                            || !AccessTokenSubject.TryGetUserId(context.Principal, out var principalUserId)
                            || payloadUserId != principalUserId)
                        {
                            context.Fail("The access token subject is invalid.");
                        }

                        return Task.CompletedTask;
                    },

                    // Owns the public 401 for every rejected or absent credential. The framework would
                    // otherwise return an empty body; this writes the standard error contract instead.
                    OnChallenge = async context =>
                    {
                        // Stops the framework from writing its own competing response.
                        context.HandleResponse();

                        // HandleResponse() skips the framework's header too, so the scheme is re-added here.
                        // Only the bare scheme: no error or error_description, which would name the failure.
                        context.Response.Headers.Append(
                            HeaderNames.WWWAuthenticate,
                            JwtBearerDefaults.AuthenticationScheme);

                        await WriteErrorAsync(
                            context.HttpContext,
                            ApiErrorResponseFactory.Unauthorized(context.HttpContext));
                    },

                    // Authentication succeeded but an authorization requirement was not met.
                    OnForbidden = context => WriteErrorAsync(
                        context.HttpContext,
                        ApiErrorResponseFactory.Forbidden(context.HttpContext))
                };
            });

        return services;
    }

    /// <summary>
    /// Writes one <see cref="ApiErrorResponse"/> as JSON for an authentication or authorization outcome, so both
    /// events use the same contract as the rest of the API instead of hand-built JSON.
    /// </summary>
    private static Task WriteErrorAsync(HttpContext httpContext, ApiErrorResponse response)
    {
        // Another component already began the response; writing again would corrupt it.
        if (httpContext.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        httpContext.Response.StatusCode = response.Status;

        // WriteAsJsonAsync sets the JSON content type, so these never fall back to text/plain or HTML.
        return httpContext.Response.WriteAsJsonAsync(response);
    }

    /// <summary>
    /// Registers the permission authorization bridge. Authorization services themselves already come from
    /// <c>AddControllers</c>, which is why <c>[Authorize]</c> works today; only the handler is added here.
    /// </summary>
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: it reaches IPermissionChecker, which is scoped around ApplicationDbContext.
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }

    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandHandler<RegisterCommand, RegisterResult>, RegisterCommandHandler>();
        services.AddScoped<ICommandHandler<VerifyPhoneCommand, PhoneVerified>, VerifyPhoneCommandHandler>();
        services.AddScoped<ICommandHandler<ResendPhoneVerificationCommand>, ResendPhoneVerificationCommandHandler>();
        services.AddScoped<ICommandHandler<VerifyEmailCommand, EmailVerified>, VerifyEmailCommandHandler>();
        services.AddScoped<ICommandHandler<ResendEmailVerificationCommand>, ResendEmailVerificationCommandHandler>();
        services.AddScoped<ICommandHandler<ForgotPasswordCommand>, ForgotPasswordCommandHandler>();
        services.AddScoped<ICommandHandler<VerifyResetCodeCommand, VerifyResetCodeResult>, VerifyResetCodeCommandHandler>();
        services.AddScoped<ICommandHandler<ResetPasswordCommand>, ResetPasswordCommandHandler>();
        services.AddScoped<ICommandHandler<LoginCommand, LoginResult>, LoginCommandHandler>();
        services.AddScoped<ICommandHandler<RefreshCommand, RefreshResult>, RefreshCommandHandler>();
        services.AddScoped<ICommandHandler<LogoutCommand>, LogoutCommandHandler>();
        services.AddScoped<IQueryHandler<GetMeQuery, MeResult>, GetMeQueryHandler>();
        services.AddScoped<IQueryHandler<GetAuthContextQuery, AuthContextResult>, GetAuthContextQueryHandler>();
        services.AddScoped<ICommandHandler<CreatePropertyCommand, CreatePropertyResult>, CreatePropertyCommandHandler>();
        services.AddScoped<IQueryHandler<GetPropertyByIdQuery, PropertyDetails>, GetPropertyByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetPropertiesQuery, PagedList<PropertyDetails>>, GetPropertiesQueryHandler>();
        services.AddScoped<IQueryHandler<GetPropertyShareQuery, PropertyDetails>, GetPropertyShareQueryHandler>();
        services.AddOptions<ShareOptions>().BindConfiguration(ShareOptions.SectionName);
        services.AddScoped<ICommandHandler<UpdatePropertyCommand, PropertyDetails>, UpdatePropertyCommandHandler>();
        services.AddScoped<ICommandHandler<ArchivePropertyCommand, PropertyDetails>, ArchivePropertyCommandHandler>();
        services.AddScoped<IQueryHandler<GetAmenitiesQuery, IReadOnlyList<AmenityDetails>>, GetAmenitiesQueryHandler>();
        services.AddScoped<IQueryHandler<GetViewsQuery, IReadOnlyList<ViewDetails>>, GetViewsQueryHandler>();
        services.AddScoped<IQueryHandler<GetLocationsQuery, IReadOnlyList<LocationDetails>>, GetLocationsQueryHandler>();
        services.AddScoped<ICommandHandler<SearchPropertiesCommand, PropertySearchResult>, SearchPropertiesCommandHandler>();
        services.AddScoped<PropertyImageAccess>();
        services.AddScoped<SmartProperty.Application.Marketplace.AuthorityRequests.OwnerAuthorityRequests>();
        services.AddScoped<SmartProperty.Application.Marketplace.AuthorityRequests.StaffAuthorityRequests>();
        services.AddScoped<ICommandHandler<UploadPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>>, UploadPropertyImagesCommandHandler>();
        services.AddScoped<ICommandHandler<DeletePropertyImageCommand, DeletePropertyImageResult>, DeletePropertyImageCommandHandler>();
        services.AddScoped<ICommandHandler<ReorderPropertyImagesCommand, IReadOnlyList<PropertyImageDetails>>, ReorderPropertyImagesCommandHandler>();
        services.AddScoped<IQueryHandler<GetPropertyImageFileQuery, PropertyImageFile>, GetPropertyImageFileQueryHandler>();
        services.AddScoped<ICommandHandler<AddFavoriteCommand, AddFavoriteResult>, AddFavoriteCommandHandler>();
        services.AddScoped<ICommandHandler<RemoveFavoriteCommand, RemoveFavoriteResult>, RemoveFavoriteCommandHandler>();
        services.AddScoped<IQueryHandler<GetFavoritesQuery, PagedList<FavoriteDetails>>, GetFavoritesQueryHandler>();
        services.AddScoped<IQueryHandler<GetFavoriteIdsQuery, IReadOnlyList<Guid>>, GetFavoriteIdsQueryHandler>();
        services.AddScoped<OwnerAccessProvisioner>();
        services.AddScoped<ICommandHandler<AddToComparisonCommand, AddToComparisonResult>, AddToComparisonCommandHandler>();
        services.AddScoped<ICommandHandler<RemoveFromComparisonCommand, PropertyComparisonDetails>, RemoveFromComparisonCommandHandler>();
        services.AddScoped<ICommandHandler<ClearComparisonCommand, PropertyComparisonDetails>, ClearComparisonCommandHandler>();
        services.AddScoped<IQueryHandler<GetComparisonQuery, PropertyComparisonDetails>, GetComparisonQueryHandler>();
        services.AddScoped<ICommandHandler<SaveOwnerIdentityCommand, OwnerProfileDetails>, SaveOwnerIdentityCommandHandler>();
        services.AddScoped<ICommandHandler<SaveOwnerAddressCommand, OwnerProfileDetails>, SaveOwnerAddressCommandHandler>();
        services.AddScoped<ICommandHandler<UploadAddressProofCommand, OwnerProfileDetails>, UploadAddressProofCommandHandler>();
        services.AddScoped<ICommandHandler<SubmitOwnerProfileCommand, OwnerProfileDetails>, SubmitOwnerProfileCommandHandler>();
        services.AddScoped<IQueryHandler<GetMyOwnerProfileQuery, OwnerProfileDetails>, GetMyOwnerProfileQueryHandler>();
        services.AddScoped<IQueryHandler<GetOwnerProfileForReviewQuery, OwnerProfileDetails>, GetOwnerProfileForReviewQueryHandler>();
        services.AddScoped<IQueryHandler<GetMyAddressProofQuery, AddressProofFile>, GetMyAddressProofQueryHandler>();
        services.AddScoped<IQueryHandler<GetAddressProofForReviewQuery, AddressProofFile>, GetAddressProofForReviewQueryHandler>();
        services.AddScoped<IQueryHandler<GetOwnerReviewQueueQuery, PagedList<OwnerReviewItem>>, GetOwnerReviewQueueQueryHandler>();
        services.AddScoped<ICommandHandler<ApproveOwnerProfileCommand, OwnerProfileDetails>, ApproveOwnerProfileCommandHandler>();
        services.AddScoped<ICommandHandler<RejectOwnerProfileCommand, OwnerProfileDetails>, RejectOwnerProfileCommandHandler>();
        services.AddScoped<ICommandHandler<RecordPropertyMatchCommand, PropertyMatchDetails>, RecordPropertyMatchCommandHandler>();

        services
            .AddScoped<ICommandHandler<ApproveWorkspaceAccessRequestCommand, ApproveWorkspaceAccessRequestResult>,
                ApproveWorkspaceAccessRequestCommandHandler>();
        services
            .AddScoped<ICommandHandler<RejectWorkspaceAccessRequestCommand, RejectWorkspaceAccessRequestResult>,
                RejectWorkspaceAccessRequestCommandHandler>();
        services
            .AddScoped<IQueryHandler<GetWorkspaceAccessRequestsQuery, PagedList<WorkspaceAccessRequestListItem>>,
                GetWorkspaceAccessRequestsQueryHandler>();

        services
            .AddScoped<IQueryHandler<GetRegistrationWorkspaceOptionsQuery, IReadOnlyList<RegistrationWorkspaceOption>>,
                GetRegistrationWorkspaceOptionsQueryHandler>();
        services.AddScoped<IQueryHandler<GetWorkspaceQuery, WorkspaceDetails>, GetWorkspaceQueryHandler>();
        services.AddScoped<IQueryHandler<GetAdminWorkspacesQuery, PagedList<AdminWorkspaceDetails>>, GetAdminWorkspacesQueryHandler>();
        services.AddScoped<IQueryHandler<GetAdminWorkspaceQuery, AdminWorkspaceDetails>, GetAdminWorkspaceQueryHandler>();
        services.AddScoped<ICommandHandler<CreateWorkspaceCommand, CreateWorkspaceResult>, CreateWorkspaceCommandHandler>();
        services.AddScoped<ICommandHandler<CreateWorkspaceMemberCommand, CreateWorkspaceMemberResult>, CreateWorkspaceMemberCommandHandler>();

        // The anonymous public marketplace. Both handlers need IPublicListingReadRepository, which Persistence
        // registers; a host without that registration does not start in Development.
        services
            .AddScoped<IQueryHandler<SearchPublicListingsQuery, PagedList<PublicListingSummary>>,
                SearchPublicListingsQueryHandler>();
        services
            .AddScoped<IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail>,
                GetPublicListingByIdQueryHandler>();

        // The authenticated submitter's own listing-submission reads, and only the reads. The acting user comes from
        // ICurrentUser alone and must be an Active account; ownership is enforced in the database by
        // ISubmitterListingSubmissionReadRepository, which Persistence registers. No permission code: the reads are
        // self-scoped. Deliberately absent: every Create/Update/Submit/Discard draft handler and every decision,
        // legal-authority, grant or publication service.
        services
            .AddScoped<IQueryHandler<GetMyListingSubmissionsQuery, PagedList<SubmitterSubmissionSummary>>,
                GetMyListingSubmissionsQueryHandler>();
        services
            .AddScoped<IQueryHandler<GetMyListingSubmissionByIdQuery, SubmitterSubmissionDetail>,
                GetMyListingSubmissionByIdQueryHandler>();

        // The Platform Admin review reads, and only the reads. Both handlers authorize through IReviewReadAuthorizer,
        // which asks the persisted Platform-scoped marketplace.submissions.review.read permission and nothing else; they
        // need IReviewerListingSubmissionReadRepository, which Persistence registers. Deliberately absent from this
        // build: any decision authorizer, Approve/Reject handler and every legal-authority, grant, publication or write
        // service. Those stay absent until their own activation is approved.
        services.AddScoped<IReviewReadAuthorizer, PlatformReviewReadAuthorizer>();
        services
            .AddScoped<IQueryHandler<GetReviewQueueQuery, PagedList<ReviewQueueItem>>,
                GetReviewQueueQueryHandler>();
        services
            .AddScoped<IQueryHandler<GetReviewSubmissionQuery, ReviewSubmissionDetail>,
                GetReviewSubmissionQueryHandler>();

        // The owner's advertisement workflow and the administrator's decisions. The evidence and independence sources are
        // registered by AddAuthorityWorkflow: the owner's documents, scanned by ClamAV, and the recorded relationships.
        // Neither invents an answer; an unscanned document or an unknown relationship is refused.
        services.AddScoped<IReviewDecisionAuthorizer, PlatformReviewDecisionAuthorizer>();

        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create.CreateListingDraftCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.ListingDraftResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create.CreateListingDraftCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth.CreatePropertyListingDraftsCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth.PropertyListingDraftsResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth.CreatePropertyListingDraftsCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update.UpdateListingDraftCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.ListingDraftResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update.UpdateListingDraftCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit.SubmitListingDraftCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit.SubmitListingDraftResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit.SubmitListingDraftCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard.DiscardListingDraftCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard.DiscardListingDraftResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard.DiscardListingDraftCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw.WithdrawListingCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw.WithdrawListingResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw.WithdrawListingCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.ApproveListingSubmissionCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.ReviewDecisionResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.ApproveListingSubmissionCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.RejectListingSubmissionCommand,
            SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.ReviewDecisionResult>,
            SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide.RejectListingSubmissionCommandHandler>();

        // Purchase and rental requests, the commitments they become, and the administrator's signing window. Every
        // handler takes the acting user from ICurrentUser alone. Reads are scoped to the caller inside the query.
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Create.CreatePurchaseRequestCommand,
            SmartProperty.Application.Marketplace.Requests.Create.RequestCreatedResult>,
            SmartProperty.Application.Marketplace.Requests.Create.CreatePurchaseRequestCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Create.CreateRentalRequestCommand,
            SmartProperty.Application.Marketplace.Requests.Create.RequestCreatedResult>,
            SmartProperty.Application.Marketplace.Requests.Create.CreateRentalRequestCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Decide.WithdrawRequestCommand,
            SmartProperty.Application.Marketplace.Requests.Decide.RequestDecisionResult>,
            SmartProperty.Application.Marketplace.Requests.Decide.WithdrawRequestCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Decide.RejectRequestCommand,
            SmartProperty.Application.Marketplace.Requests.Decide.RequestDecisionResult>,
            SmartProperty.Application.Marketplace.Requests.Decide.RejectRequestCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Decide.AcceptRequestCommand,
            SmartProperty.Application.Marketplace.Requests.Decide.AcceptRequestResult>,
            SmartProperty.Application.Marketplace.Requests.Decide.AcceptRequestCommandHandler>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Decide.ConfirmCommitmentStepCommand,
            SmartProperty.Application.Marketplace.Requests.Decide.CommitmentStepResult>,
            SmartProperty.Application.Marketplace.Requests.Decide.ConfirmCommitmentStepCommandHandler>();

        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetMyRequestsQuery,
            PagedList<SmartProperty.Application.Marketplace.Requests.MarketplaceRequestView>>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();
        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetMyRequestQuery,
            SmartProperty.Application.Marketplace.Requests.MarketplaceRequestView>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();
        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetReceivedRequestsQuery,
            PagedList<SmartProperty.Application.Marketplace.Requests.MarketplaceRequestView>>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();
        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetReceivedRequestQuery,
            SmartProperty.Application.Marketplace.Requests.MarketplaceRequestView>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();
        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetMyCommitmentsQuery,
            PagedList<SmartProperty.Application.Marketplace.Requests.CommitmentView>>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();
        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Read.GetMyCommitmentQuery,
            SmartProperty.Application.Marketplace.Requests.CommitmentView>,
            SmartProperty.Application.Marketplace.Requests.Read.RequestReadHandlers>();

        services.AddScoped<IQueryHandler<SmartProperty.Application.Marketplace.Requests.Settings.GetSigningWindowQuery,
            SmartProperty.Application.Marketplace.Requests.Settings.SigningWindowResult>,
            SmartProperty.Application.Marketplace.Requests.Settings.SigningWindowHandlers>();
        services.AddScoped<ICommandHandler<SmartProperty.Application.Marketplace.Requests.Settings.SetSigningWindowCommand,
            SmartProperty.Application.Marketplace.Requests.Settings.SigningWindowResult>,
            SmartProperty.Application.Marketplace.Requests.Settings.SigningWindowHandlers>();

        services
            .AddScoped<ICommandHandler<BootstrapPlatformAdminCommand, BootstrapPlatformAdminResult>,
                BootstrapPlatformAdminCommandHandler>();

        return services;
    }

    /// <summary>
    /// Binds the <c>Bootstrap</c> section and registers the one-time platform administrator provisioning that runs
    /// at startup only when that section explicitly enables it.
    /// </summary>
    /// <remarks>
    /// Registration is unconditional; the behaviour is not. With <c>Bootstrap:Enabled</c> false — the default in
    /// code and in appsettings.json — the hosted service does nothing at all.
    ///
    /// The validation runs through <c>ValidateOnStart</c>, like the <c>Jwt</c> options, so an enabled bootstrap
    /// missing its email stops the host before it serves a request rather than at the moment it would have run.
    /// </remarks>
    public static IServiceCollection AddPlatformAdminBootstrap(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BootstrapOptions>()
            .Bind(configuration.GetSection(BootstrapOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{BootstrapOptions.SectionName} configuration is invalid. When "
                + $"{BootstrapOptions.SectionName}__Enabled is true, "
                + $"{BootstrapOptions.SectionName}__PlatformAdminEmail must name an existing registered user. "
                + "Bootstrap never creates an account, a password, or any other credential.")
            .ValidateOnStart();

        services.AddHostedService<PlatformAdminBootstrapService>();

        return services;
    }
}
