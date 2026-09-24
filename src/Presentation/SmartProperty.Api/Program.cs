using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using SmartProperty.Api.Infrastructure;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Persistence;

const string BearerSecuritySchemeName = "Bearer";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        var components = document.Components;

        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        components.SecuritySchemes[BearerSecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = JwtBearerDefaults.AuthenticationScheme.ToLowerInvariant(),
            BearerFormat = "JWT"
        };

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        var requiresAuthorization = metadata.OfType<IAuthorizeData>().Any();

        if (allowsAnonymous || !requiresAuthorization)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(BearerSecuritySchemeName, context.Document)] = []
        });

        return Task.CompletedTask;
    });
});

builder.Services.AddApiConventions();
builder.Services.AddDateTimeProvider();
builder.Services.AddApiAuthentication(builder.Configuration);
builder.Services.AddApiAuthorization();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddApplicationHandlers();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode =
            StatusCodes.Status500InternalServerError;

        var response =
            ApiErrorResponseFactory.UnexpectedFailure(context);

        await context.Response.WriteAsJsonAsync(response);
    });
});

if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
// Explicit placement keeps authentication after correlation ID and exception handling;
// otherwise WebApplication inserts these ahead of all other middleware.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration =>
        registration.Tags.Contains("ready")
});

app.Run();

public partial class Program
{
}
