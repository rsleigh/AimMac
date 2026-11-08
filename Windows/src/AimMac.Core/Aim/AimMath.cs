namespace AimMac.Core.Aim;

/// <summary>1€ filter, same constants and recurrence as Mac/Sources/OneEuro.swift.</summary>
public sealed class OneEuro
{
    public double MinCutoff { get; set; }
    public double Beta { get; set; }
    private const double DCutoff = 1.0;
    private double? _xPrev;
    private double _dxPrev;
    private double _tPrev;

    public OneEuro(double minCutoff, double beta)
    {
        MinCutoff = minCutoff;
        Beta = beta;
    }

    public double Filter(double x, double t)
    {
        if (_xPrev is not double xp)
        {
            _xPrev = x;
            _tPrev = t;
            return x;
        }

        var dt = Math.Min(Math.Max(t - _tPrev, 0.001), 0.1);
        _tPrev = t;
        var dx = (x - xp) / dt;
        var ad = Alpha(DCutoff, dt);
        _dxPrev = ad * dx + (1.0 - ad) * _dxPrev;
        var a = Alpha(MinCutoff + Beta * Math.Abs(_dxPrev), dt);
        var result = a * x + (1.0 - a) * xp;
        _xPrev = result;
        return result;
    }

    public void Reset()
    {
        _xPrev = null;
        _dxPrev = 0;
    }

    private static double Alpha(double cutoff, double dt)
    {
        var tau = 1.0 / (2.0 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}

public readonly record struct DisplayRect(double X, double Y, double Width, double Height)
{
    public double MidX => X + Width / 2.0;
    public double MidY => Y + Height / 2.0;

    public bool Contains(double x, double y) =>
        x >= X && y >= Y && x < X + Width && y < Y + Height;
}

public readonly record struct Vec(double X, double Y);

public interface IDisplaySource
{
    DisplayRect Primary { get; }
    IReadOnlyList<DisplayRect> All { get; }
}

public static class AimProjection
{
    public static double Wrap(double degrees)
    {
        var x = degrees % 360.0;
        if (x > 180) x -= 360;
        if (x < -180) x += 360;
        return x;
    }

    /// <summary>
    /// Laser projection used by PointerEngine.project. xDeg &gt; 0 is right, yDeg &gt; 0 is down.
    /// </summary>
    public static Vec Project(double xDeg, double yDeg, double rangeDeg, DisplayRect main)
    {
        var r = Math.Clamp(rangeDeg, 5, 80) * Math.PI / 180.0;
        var d = main.Width / 2.0 / Math.Tan(r);
        var ax = Math.Clamp(xDeg, -80, 80) * Math.PI / 180.0;
        var ay = Math.Clamp(yDeg, -80, 80) * Math.PI / 180.0;
        return new Vec(main.MidX + d * Math.Tan(ax), main.MidY + d * Math.Tan(ay));
    }

    public static Vec ClampToDisplays(Vec point, IReadOnlyList<DisplayRect> rects)
    {
        if (rects.Count == 0)
        {
            return point;
        }

        foreach (var rect in rects)
        {
            if (rect.Contains(point.X, point.Y))
            {
                return point;
            }
        }

        var best = point;
        var bestDist = double.PositiveInfinity;
        foreach (var rect in rects)
        {
            var x = Math.Min(Math.Max(point.X, rect.X), rect.X + rect.Width - 1);
            var y = Math.Min(Math.Max(point.Y, rect.Y), rect.Y + rect.Height - 1);
            var dx = x - point.X;
            var dy = y - point.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = new Vec(x, y);
            }
        }

        return best;
    }
}

public sealed class AimTuning
{
    public static readonly double[] Ranges = [18, 28, 40];
    public static readonly (double Cutoff, double Beta)[] Smooth =
    [
        (2.5, 0.03),
        (1.2, 0.02),
        (0.6, 0.01),
    ];

    public int RangeIndex { get; set; } = 1;
    public int SmoothIndex { get; set; } = 1;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }

    public double RangeDegrees => Ranges[Math.Clamp(RangeIndex, 0, Ranges.Length - 1)];

    public (double Cutoff, double Beta) Smoothing => Smooth[Math.Clamp(SmoothIndex, 0, Smooth.Length - 1)];
}
