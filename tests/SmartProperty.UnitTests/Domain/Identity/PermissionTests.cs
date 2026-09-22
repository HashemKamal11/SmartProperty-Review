using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Identity;

public sealed class PermissionTests
{
    [Fact]
    public void Constructor_TrimsCode()
    {
        var permission = new Permission(Guid.NewGuid(), "  property.read  ");

        Assert.Equal("property.read", permission.Code);
    }

    [Fact]
    public void Constructor_PreservesCodeCasing()
    {
        // Permission codes are compared exactly; the domain must not lower-case them.
        // See docs/authorization-model.md.
        var permission = new Permission(Guid.NewGuid(), "Property.Read");

        Assert.Equal("Property.Read", permission.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsBlankCode(string code)
    {
        Assert.Throws<ArgumentException>(() => new Permission(Guid.NewGuid(), code));
    }

    [Fact]
    public void Constructor_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() => new Permission(Guid.Empty, "property.read"));
    }

    [Fact]
    public void Constructor_TrimsDescription()
    {
        var permission = new Permission(Guid.NewGuid(), "property.read", "  Read properties.  ");

        Assert.Equal("Read properties.", permission.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_TreatsBlankDescriptionAsAbsent(string? description)
    {
        var permission = new Permission(Guid.NewGuid(), "property.read", description);

        Assert.Null(permission.Description);
    }

    [Fact]
    public void UpdateDescription_ReplacesDescription()
    {
        var permission = new Permission(Guid.NewGuid(), "property.read", "Read properties.");

        permission.UpdateDescription("  Read property records.  ");

        Assert.Equal("Read property records.", permission.Description);
    }

    [Fact]
    public void UpdateDescription_ClearsDescriptionWhenBlank()
    {
        var permission = new Permission(Guid.NewGuid(), "property.read", "Read properties.");

        permission.UpdateDescription("   ");

        Assert.Null(permission.Description);
    }
}
