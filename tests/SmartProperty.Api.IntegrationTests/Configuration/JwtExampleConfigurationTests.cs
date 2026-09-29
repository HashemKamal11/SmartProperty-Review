using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Configuration;

public sealed class JwtExampleConfigurationTests
{
    private const string JwtSigningKeyVariable = "JWT_SIGNING_KEY";

    [Fact]
    public async Task ExactEnvExampleSigningKey_IsRejectedByProductionStartupValidation()
    {
        var exampleSigningKey = ReadEnvExampleValue(JwtSigningKeyVariable);

        using var host = BuildHost(exampleSigningKey);

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task SufficientlyLongTestSigningKey_IsAcceptedByProductionStartupValidation()
    {
        using var host = BuildHost(TestJwt.SigningKey);

        await host.StartAsync();
        await host.StopAsync();
    }

    private static IHost BuildHost(string signingKey)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = TestJwt.Issuer,
            ["Jwt:Audience"] = TestJwt.Audience,
            ["Jwt:SigningKey"] = signingKey,
            ["Jwt:AccessTokenLifetime"] = TestJwt.AccessTokenLifetime.ToString(),
            ["Jwt:RefreshTokenLifetime"] = TestJwt.RefreshTokenLifetime.ToString()
        });

        builder.Services.AddDateTimeProvider();
        builder.Services.AddApiAuthentication(builder.Configuration);

        return builder.Build();
    }

    private static string ReadEnvExampleValue(string variableName)
    {
        var prefix = $"{variableName}=";
        var envExamplePath = Path.Combine(AppContext.BaseDirectory, ".env.example");
        var matchingLine = File.ReadLines(envExamplePath)
            .Single(line => line.StartsWith(prefix, StringComparison.Ordinal));

        return matchingLine[prefix.Length..];
    }
}
