using SmartProperty.Api.Infrastructure.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authorization;

/// <summary>
/// The requirement is developer-supplied server configuration, not user input: a blank code is a programming
/// error and must throw rather than produce any HTTP response.
/// </summary>
public sealed class PermissionRequirementTests
{
    [Fact]
    public void Constructor_KeepsTheCode()
    {
        Assert.Equal("property.read", new PermissionRequirement("property.read").PermissionCode);
    }

    [Fact]
    public void Constructor_TrimsTheCodeAndPreservesItsCasing()
    {
        // Codes are compared exactly against Permission.Code; only surrounding whitespace is removed.
        Assert.Equal("Property.Read", new PermissionRequirement("  Property.Read  ").PermissionCode);
    }

    [Fact]
    public void Constructor_RejectsANullCode()
    {
        Assert.Throws<ArgumentException>(() => new PermissionRequirement(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Constructor_RejectsABlankCode(string permissionCode)
    {
        Assert.Throws<ArgumentException>(() => new PermissionRequirement(permissionCode));
    }
}
