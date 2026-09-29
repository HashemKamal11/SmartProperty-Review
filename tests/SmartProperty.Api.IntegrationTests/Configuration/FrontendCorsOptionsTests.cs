using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Cors;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Configuration;

public sealed class FrontendCorsOptionsTests
{
    [Fact]
    public void ValidOrigins_AreTrimmedAndDeduplicated()
    {
        var options = new FrontendCorsOptions
        {
            AllowedOrigins =
            [
                "  https://frontend.example.test  ",
                "https://admin.example.test:8443",
                "https://frontend.example.test"
            ]
        };

        Assert.Equal(
            ["https://frontend.example.test", "https://admin.example.test:8443"],
            options.GetNormalizedAllowedOrigins());
    }

    [Fact]
    public void BlankOrigins_AreIgnoredForAFailClosedEmptyPolicy()
    {
        var options = new FrontendCorsOptions
        {
            AllowedOrigins = [string.Empty, "  ", "\t"]
        };

        Assert.Empty(options.GetNormalizedAllowedOrigins());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    public void WildcardOrigins_AreRejected(string origin)
    {
        var options = new FrontendCorsOptions { AllowedOrigins = [origin] };

        Assert.Throws<OptionsValidationException>(() => options.GetNormalizedAllowedOrigins());
    }

    [Theory]
    [InlineData("not-an-origin")]
    [InlineData("ftp://frontend.example.test")]
    [InlineData("https://frontend.example.test/")]
    [InlineData("https://frontend.example.test/path")]
    [InlineData("https://frontend.example.test?mode=test")]
    [InlineData("https://user@frontend.example.test")]
    public void MalformedOrNonOriginValues_AreRejected(string origin)
    {
        var options = new FrontendCorsOptions { AllowedOrigins = [origin] };

        var exception = Assert.Throws<OptionsValidationException>(() => options.GetNormalizedAllowedOrigins());

        Assert.Contains("Cors:AllowedOrigins", exception.Message, StringComparison.Ordinal);
    }
}
