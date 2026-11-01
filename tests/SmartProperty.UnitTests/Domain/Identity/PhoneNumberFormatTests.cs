using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Identity;

public sealed class PhoneNumberFormatTests
{
    [Theory]
    [InlineData("+201012345678", "+201012345678")]
    [InlineData("+1 (415) 555-0132", "+14155550132")]
    [InlineData("  +44 20 7946 0958 ", "+442079460958")]
    [InlineData("+966.50.123.4567", "+966501234567")]
    public void ValidInternationalNumber_IsNormalizedToE164(string input, string expected)
    {
        Assert.True(PhoneNumberFormat.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01012345678")]
    [InlineData("00201012345678")]
    [InlineData("+0201012345678")]
    [InlineData("+12345")]
    [InlineData("+1234567890123456")]
    [InlineData("+20101234abcd")]
    [InlineData("+20+1012345678")]
    public void InvalidNumber_IsRejected(string? input)
    {
        Assert.False(PhoneNumberFormat.TryNormalize(input, out _));
    }

    [Fact]
    public void User_StoresTheNormalizedNumber_AndStartsUnverified()
    {
        var user = new User(Guid.NewGuid(), "a@example.test", "A", "B", DateTimeOffset.UnixEpoch, "+20 101 234 5678");

        Assert.Equal("+201012345678", user.PhoneNumber);
        Assert.False(user.IsPhoneVerified);
    }

    [Fact]
    public void User_VerifyPhone_RecordsWhenItWasVerified()
    {
        var at = DateTimeOffset.UnixEpoch.AddMinutes(3);
        var user = new User(Guid.NewGuid(), "a@example.test", "A", "B", DateTimeOffset.UnixEpoch, "+201012345678");

        user.VerifyPhone(at);

        Assert.True(user.IsPhoneVerified);
        Assert.Equal(at, user.PhoneVerifiedAt);
    }

    [Fact]
    public void User_WithoutAPhoneNumber_CannotVerifyOne()
    {
        var user = new User(Guid.NewGuid(), "a@example.test", "A", "B", DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() => user.VerifyPhone(DateTimeOffset.UnixEpoch));
    }
}
