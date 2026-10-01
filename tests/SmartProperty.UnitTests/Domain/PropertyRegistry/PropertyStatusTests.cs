using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Pins the initial registry lifecycle. Draft, pending, verified, and deleted states are deliberately absent:
/// verification belongs to future workflow functionality and archiving replaces deletion.
/// </summary>
public sealed class PropertyStatusTests
{
    [Fact]
    public void PropertyStatus_DefinesExactlyActiveAndArchived()
    {
        Assert.Equal(
            new[] { PropertyStatus.Active, PropertyStatus.Archived },
            Enum.GetValues<PropertyStatus>());
    }
}
