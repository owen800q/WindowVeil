namespace WindowVeil.Ocr.Paddle;

/// <summary>
/// Minimal port of Clipper 6.4.2 <c>ClipperOffset</c> (as wrapped by pyclipper 1.x) for the one case PaddleOCR's
/// <c>DBPostProcess.unclip</c> needs: offsetting a single closed polygon outward with round joins
/// (<c>JT_ROUND</c>, <c>ET_CLOSEDPOLYGON</c>, ArcTolerance 0.25, MiterLimit 2).
/// </summary>
/// <remarks>
/// Like pyclipper, input coordinates are converted to integers by truncation toward zero and output vertices are
/// rounded half away from zero. The final union Clipper performs to remove self-intersections is not reproduced: for the
/// convex quadrilaterals produced by the detector the raw offset path is already simple, and only its convex hull is used
/// afterwards (by the min-area rectangle).
/// </remarks>
internal static class ClipperOffset
{
    private const double ArcTolerance = 0.25;
    private const double DefArcTolerance = 0.25;

    /// <summary>Offsets <paramref name="polygon"/> by <paramref name="delta"/> (positive grows). Returns an empty list when the path is degenerate.</summary>
    public static List<(long X, long Y)> OffsetRound(IReadOnlyList<(float X, float Y)> polygon, double delta)
    {
        // pyclipper: IntPoint(<cInt>x, <cInt>y) truncates toward zero.
        var path = polygon.Select(p => ((long)p.X, (long)p.Y)).ToList();
        var src = StripDuplicates(path);
        if (src.Count < 3) return [];
        if (Area(src) < 0) src.Reverse(); // FixOrientations: the (only) polygon must have positive orientation.
        if (Math.Abs(delta) < 1e-20) return src;

        double y = ArcTolerance > Math.Abs(delta) * DefArcTolerance ? Math.Abs(delta) * DefArcTolerance : ArcTolerance;
        double steps = Math.PI / Math.Acos(1 - y / Math.Abs(delta));
        if (steps > Math.Abs(delta) * Math.PI) steps = Math.Abs(delta) * Math.PI;
        double sin = Math.Sin(2 * Math.PI / steps), cos = Math.Cos(2 * Math.PI / steps);
        double stepsPerRad = steps / (2 * Math.PI);
        if (delta < 0) sin = -sin;

        int len = src.Count;
        var normals = new (double X, double Y)[len];
        for (int j = 0; j < len - 1; j++) normals[j] = UnitNormal(src[j], src[j + 1]);
        normals[len - 1] = UnitNormal(src[len - 1], src[0]);

        var dest = new List<(long X, long Y)>(len * 4);
        int k = len - 1;
        for (int j = 0; j < len; j++)
        {
            double sinA = normals[k].X * normals[j].Y - normals[j].X * normals[k].Y;
            bool done = false;
            if (Math.Abs(sinA * delta) < 1.0)
            {
                double cosA0 = normals[k].X * normals[j].X + normals[j].Y * normals[k].Y;
                if (cosA0 > 0)
                {
                    dest.Add((Round(src[j].X + normals[k].X * delta), Round(src[j].Y + normals[k].Y * delta)));
                    done = true;
                }
            }
            else if (sinA > 1.0) sinA = 1.0;
            else if (sinA < -1.0) sinA = -1.0;

            if (!done)
            {
                if (sinA * delta < 0)
                {
                    dest.Add((Round(src[j].X + normals[k].X * delta), Round(src[j].Y + normals[k].Y * delta)));
                    dest.Add(src[j]);
                    dest.Add((Round(src[j].X + normals[j].X * delta), Round(src[j].Y + normals[j].Y * delta)));
                }
                else
                {
                    // DoRound
                    double a = Math.Atan2(sinA, normals[k].X * normals[j].X + normals[k].Y * normals[j].Y);
                    int stepCount = Math.Max((int)Round(stepsPerRad * Math.Abs(a)), 1);
                    double x = normals[k].X, yy = normals[k].Y;
                    for (int i = 0; i < stepCount; i++)
                    {
                        dest.Add((Round(src[j].X + x * delta), Round(src[j].Y + yy * delta)));
                        double x2 = x;
                        x = x * cos - sin * yy;
                        yy = x2 * sin + yy * cos;
                    }
                    dest.Add((Round(src[j].X + normals[j].X * delta), Round(src[j].Y + normals[j].Y * delta)));
                }
            }
            k = j;
        }
        return dest;
    }

    private static List<(long X, long Y)> StripDuplicates(List<(long X, long Y)> path)
    {
        int high = path.Count - 1;
        while (high > 0 && path[0] == path[high]) high--;
        var result = new List<(long X, long Y)>(high + 1) { path[0] };
        for (int i = 1; i <= high; i++)
            if (result[^1] != path[i]) result.Add(path[i]);
        return result;
    }

    /// <summary>Clipper <c>Area</c> (positive for the orientation Clipper calls "true").</summary>
    private static double Area(List<(long X, long Y)> poly)
    {
        int size = poly.Count;
        if (size < 3) return 0;
        double a = 0;
        for (int i = 0, j = size - 1; i < size; ++i)
        {
            a += ((double)poly[j].X + poly[i].X) * ((double)poly[j].Y - poly[i].Y);
            j = i;
        }
        return -a * 0.5;
    }

    private static (double X, double Y) UnitNormal((long X, long Y) p1, (long X, long Y) p2)
    {
        if (p1 == p2) return (0, 0);
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double f = 1.0 / Math.Sqrt(dx * dx + dy * dy);
        dx *= f; dy *= f;
        return (dy, -dx);
    }

    /// <summary>Clipper <c>Round</c>: half away from zero.</summary>
    private static long Round(double v) => v < 0 ? (long)(v - 0.5) : (long)(v + 0.5);
}
