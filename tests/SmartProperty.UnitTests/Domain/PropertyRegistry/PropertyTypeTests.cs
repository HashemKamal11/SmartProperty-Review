using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Pins the physical-form axis. Usage classification (residential, commercial, industrial) is a separate
/// future concept and must never appear here.
/// </summary>
public sealed class PropertyTypeTests
{
    [Theory]
    [InlineData(PropertyType.Land)]
    [InlineData(PropertyType.Building)]
    [InlineData(PropertyType.Unit)]
    public void PropertyType_DefinesThePhysicalForms(PropertyType type)
    {
        Assert.True(Enum.IsDefined(type));
    }

    [Fact]
    public void PropertyType_DefinesExactlyThreeValues()
    {
        Assert.Equal(
            new[] { PropertyType.Land, PropertyType.Building, PropertyType.Unit },
            Enum.GetValues<PropertyType>());
    }
}
