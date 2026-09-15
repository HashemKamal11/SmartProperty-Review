
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SmartProperty.Api.Infrastructure;
using SmartProperty.Api.Infrastructure.Errors;
using SmartProperty.Api.Infrastructure.Http;
using SmartProperty.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddApiConventions();
builder.Services.AddPersistence(builder.Configuration);

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
