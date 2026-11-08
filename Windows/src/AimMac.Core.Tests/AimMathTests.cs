using AimMac.Core.Aim;
using Xunit;

namespace AimMac.Core.Tests;

public class AimMathTests
{
    private static readonly DisplayRect Main = new(0, 0, 1920, 1080);

    [Fact]
    public void ProjectionOfZeroIsTheMainDisplayCenter()
    {
        var point = AimProjection.Project(0, 0, 28, Main);
        Assert.Equal(960, point.X, 3);
        Assert.Equal(540, point.Y, 3);
    }

    [Fact]
    public void FullRangeReachesTheRightEdgeBeforeClamp()
    {
        var point = AimProjection.Project(28, 0, 28, Main);
        Assert.Equal(1920, point.X, 3);
        var clamped = AimProjection.ClampToDisplays(point, [Main]);
        Assert.Equal(1919, clamped.X, 3);
        Assert.InRange(clamped.Y, 539, 541);
    }

    [Fact]
    public void ClampKeepsAPointOnASecondDisplay()
    {
        var second = new DisplayRect(1920, 0, 800, 1080);
        var inside = AimProjection.ClampToDisplays(new Vec(2400, 100), [Main, second]);
        Assert.Equal(2400, inside.X);
        var outside = AimProjection.ClampToDisplays(new Vec(4000, 100), [Main, second]);
        Assert.Equal(2719, outside.X, 3);
        Assert.Equal(100, outside.Y, 3);
    }

    [Fact]
    public void WrapFoldsPast180()
    {
        Assert.Equal(-170, AimProjection.Wrap(190), 6);
        Assert.Equal(170, AimProjection.Wrap(-190), 6);
        Assert.Equal(0, AimProjection.Wrap(360), 6);
    }

    [Fact]
    public void OneEuroReturnsTheFirstSampleUnchanged()
    {
        var filter = new OneEuro(1.2, 0.02);
        Assert.Equal(4, filter.Filter(4, 1));
        var next = filter.Filter(10, 1.01);
        Assert.InRange(next, 4, 10);
        filter.Reset();
        Assert.Equal(1, filter.Filter(1, 5));
    }
}
