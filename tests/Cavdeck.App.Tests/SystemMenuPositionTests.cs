using System.Windows;
using System.Windows.Media;
using Cavdeck.App.Controls;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class SystemMenuPositionTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Menu_location_uses_logical_screen_coordinates_at_each_dpi(double scale)
    {
        // PointToScreen provides physical pixels; ShowSystemMenu expects logical.
        var physical = new Point(1600 * scale, 60 * scale);
        var fromDevice = new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0);

        var logical = AppWindow.DeviceToLogicalScreenPoint(physical, fromDevice);

        Assert.Equal(1600, logical.X, 6);
        Assert.Equal(60, logical.Y, 6);
    }

    [Fact]
    public void Negative_secondary_monitor_coordinates_are_preserved()
    {
        var fromDevice = new Matrix(0.8, 0, 0, 0.8, 0, 0);
        var physical = new Point(-1920, 600);

        var logical = AppWindow.DeviceToLogicalScreenPoint(physical, fromDevice);

        Assert.Equal(-1536, logical.X, 6);
        Assert.Equal(480, logical.Y, 6);
    }

    [Fact]
    public void Coordinate_conversion_is_not_applied_twice()
    {
        var fromDevice = new Matrix(2.0 / 3.0, 0, 0, 2.0 / 3.0, 0, 0);
        var physical = new Point(1200, 72);
        var logical = AppWindow.DeviceToLogicalScreenPoint(physical, fromDevice);

        Assert.Equal(800, logical.X, 6);
        Assert.Equal(48, logical.Y, 6);
        Assert.NotEqual(physical, logical);
    }
}
