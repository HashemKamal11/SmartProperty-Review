using SmartProperty.Application.Authentication.Register;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// A password shorter than the minimum is refused at registration (and, through the same validator, when an
/// administrator creates a member), so a one-character password can never become a credential.
/// </summary>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("1234567")]
    public void ShortPassword_IsRejected(string password)
    {
        var error = RegisterCommandValidator.Validate(CommandWith(password));

        Assert.NotNull(error);
        Assert.Equal("validation.failed", error!.Code);
        Assert.Contains("at least", error.Description);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("correct-horse-battery-staple")]
    public void PasswordAtOrAboveTheMinimum_IsAccepted(string password)
    {
        Assert.Null(RegisterCommandValidator.Validate(CommandWith(password)));
    }

    [Fact]
    public void NewAccountValidator_AppliesTheSameMinimum()
    {
        Assert.NotNull(NewAccountValidator.Validate("a@example.com", "short", "A", "B"));
        Assert.Null(NewAccountValidator.Validate("a@example.com", "long-enough-pw", "A", "B"));
    }

    private static RegisterCommand CommandWith(string password) =>
        new("owner@example.com", password, "Owner", "Example", "+201012345678");
}
