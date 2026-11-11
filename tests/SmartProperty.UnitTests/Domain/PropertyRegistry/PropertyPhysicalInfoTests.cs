using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

public sealed class PropertyPhysicalInfoTests
{
    [Fact]
    public void Empty_HoldsNothing()
    {
        var empty = PropertyPhysicalInfo.Empty;

        Assert.Null(empty.AreaSqm);
        Assert.Null(empty.Floor);
        Assert.Null(empty.Furnished);
    }

    [Fact]
    public void Constructor_RetainsEveryValue()
    {
        var info = new PropertyPhysicalInfo(120.5m, 3, true);

        Assert.Equal(120.5m, info.AreaSqm);
        Assert.Equal(3, info.Floor);
        Assert.True(info.Furnished);
    }

    [Fact]
    public void Records_AreComparedByValue()
    {
        Assert.Equal(new PropertyPhysicalInfo(50m, 2, false), new PropertyPhysicalInfo(50m, 2, false));
        Assert.NotEqual(new PropertyPhysicalInfo(50m, 2, false), new PropertyPhysicalInfo(51m, 2, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public void Constructor_RejectsAnAreaOutsideTheSupportedRange(int area)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyPhysicalInfo(areaSqm: area));
    }

    [Fact]
    public void Constructor_AcceptsTheLargestSupportedArea()
    {
        Assert.Equal(PropertyPhysicalInfo.MaxAreaSqm, new PropertyPhysicalInfo(areaSqm: PropertyPhysicalInfo.MaxAreaSqm).AreaSqm);
    }

    [Theory]
    [InlineData(PropertyPhysicalInfo.MinFloor - 1)]
    [InlineData(PropertyPhysicalInfo.MaxFloor + 1)]
    public void Constructor_RejectsAFloorOutsideTheSupportedRange(int floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyPhysicalInfo(floor: floor));
    }

    [Theory]
    [InlineData(PropertyPhysicalInfo.MinFloor)]
    [InlineData(0)]
    [InlineData(PropertyPhysicalInfo.MaxFloor)]
    public void Constructor_AcceptsTheBoundaryFloors(int floor)
    {
        Assert.Equal(floor, new PropertyPhysicalInfo(floor: floor).Floor);
    }

    [Fact]
    public void ThePropertysOwnTimeZoneIsKeptAndTrimmed()
    {
        Assert.Equal("Africa/Cairo", new PropertyPhysicalInfo(timeZoneId: " Africa/Cairo ").TimeZoneId);
        Assert.Null(PropertyPhysicalInfo.Empty.TimeZoneId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankTimeZoneMeansTheCountrysOwn(string? zone)
    {
        Assert.Null(new PropertyPhysicalInfo(timeZoneId: zone).TimeZoneId);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("Cairo")]
    [InlineData("UTC+2")]
    public void AnUnknownTimeZoneIsRefused(string zone)
    {
        Assert.Throws<ArgumentException>(() => new PropertyPhysicalInfo(timeZoneId: zone));
    }

    [Fact]
    public void TheTimeZoneCountsAsAChangeWhenComparedByValue()
    {
        Assert.NotEqual(new PropertyPhysicalInfo(50m, timeZoneId: "Africa/Cairo"), new PropertyPhysicalInfo(50m, timeZoneId: "Asia/Riyadh"));
        Assert.Equal(new PropertyPhysicalInfo(50m, timeZoneId: "Africa/Cairo"), new PropertyPhysicalInfo(50m, timeZoneId: "Africa/Cairo"));
    }
}
