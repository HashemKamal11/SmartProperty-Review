#nullable enable
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Time;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Login;
using SmartProperty.Application.Authentication.Register;

namespace SmartProperty.Api.Infrastructure;

internal static class ServiceCollectionExtensions
{
    // Tolerance for clock drift between token issuer and validator.
    private static readonly TimeSpan AccessTokenClockSkew = TimeSpan.FromSeconds(30);

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
        services.AddSingleton<ITokenProvider, TokenProvider>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptionsAccessor) =>
            {
                var jwtOptions = jwtOptionsAccessor.Value;

                // Keep JWT claim names as issued so "sub" is read consistently by CurrentUser.
                bearerOptions.MapInboundClaims = false;

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
                    }
                };
            });

        return services;
    }

    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandHandler<RegisterCommand, RegisterResult>, RegisterCommandHandler>();
        services.AddScoped<ICommandHandler<LoginCommand, LoginResult>, LoginCommandHandler>();

        return services;
    }
}
