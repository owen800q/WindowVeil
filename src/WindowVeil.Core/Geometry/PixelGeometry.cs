namespace WindowVeil.Geometry;

/// <summary>
/// An integer point in physical pixels. Unless documented otherwise, WindowVeil uses
/// <b>physical virtual-desktop screen coordinates</b>: the origin is the top-left of the primary
/// monitor and coordinates can be negative on monitors to its left or above it.
/// </summary>
public readonly record struct PixelPoint(int X, int Y)
{
    /// <summary>Returns this point moved by the given delta.</summary>
    public PixelPoint Offset(int dx, int dy) => new(X + dx, Y + dy);

    /// <inheritdoc />
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>An integer size in physical pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    /// <summary>True when either dimension is zero or negative.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// An axis-aligned integer rectangle in physical pixels (see <see cref="PixelPoint"/> for the
/// default coordinate space). <see cref="Right"/> and <see cref="Bottom"/> are exclusive.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>The empty rectangle at the origin.</summary>
    public static PixelRect Empty => default;

    /// <summary>Exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>Exclusive bottom edge.</summary>
    public int Bottom => Y + Height;

    /// <summary>Top-left corner.</summary>
    public PixelPoint Location => new(X, Y);

    /// <summary>Size of the rectangle.</summary>
    public PixelSize Size => new(Width, Height);

    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Center point (rounded down).</summary>
    public PixelPoint Center => new(X + Width / 2, Y + Height / 2);

    /// <summary>Area in pixels (0 for empty rectangles).</summary>
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    /// <summary>Creates a rectangle from left/top/right/bottom edges (right/bottom exclusive).</summary>
    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>Returns this rectangle moved by the given delta.</summary>
    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Grows the rectangle by the given amounts on each side (negative values shrink it).</summary>
    public PixelRect Inflate(int left, int top, int right, int bottom) =>
        FromLTRB(X - left, Y - top, Right + right, Bottom + bottom);

    /// <summary>Grows the rectangle uniformly.</summary>
    public PixelRect Inflate(int amount) => Inflate(amount, amount, amount, amount);

    /// <summary>True when the point lies inside the rectangle.</summary>
    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    /// <summary>True when <paramref name="other"/> lies completely inside this rectangle.</summary>
    public bool Contains(PixelRect other) =>
        !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>True when the two rectangles share at least one pixel.</summary>
    public bool IntersectsWith(PixelRect other) =>
        !IsEmpty && !other.IsEmpty && other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <summary>Intersection of the two rectangles, or <see cref="Empty"/> when they do not overlap.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        if (!IntersectsWith(other)) return Empty;
        return FromLTRB(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
    }

    /// <summary>Smallest rectangle containing both rectangles. Empty inputs are ignored.</summary>
    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>Smallest rectangle containing all non-empty rectangles in the sequence.</summary>
    public static PixelRect UnionAll(IEnumerable<PixelRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        var result = Empty;
        foreach (var r in rects) result = result.Union(r);
        return result;
    }

    /// <summary>Rounds a floating-point rectangle outward so the result fully covers it.</summary>
    public static PixelRect FromRectDOutward(RectD rect)
    {
        int left = (int)Math.Floor(rect.X);
        int top = (int)Math.Floor(rect.Y);
        int right = (int)Math.Ceiling(rect.X + rect.Width);
        int bottom = (int)Math.Ceiling(rect.Y + rect.Height);
        return FromLTRB(left, top, right, bottom);
    }

    /// <summary>Converts to a floating-point rectangle in the same coordinate space.</summary>
    public RectD ToRectD() => new(X, Y, Width, Height);

    /// <inheritdoc />
    public override string ToString() => $"[{X}, {Y}, {Width}x{Height}]";
}

/// <summary>
/// A floating-point point. The coordinate space is defined by the API that produces it
/// (for example OCR results use image-local pixels).
/// </summary>
public readonly record struct PointD(double X, double Y)
{
    /// <inheritdoc />
    public override string ToString() => $"({X:0.##}, {Y:0.##})";
}

/// <summary>A floating-point rectangle. The coordinate space is defined by the producing API.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    /// <summary>Exclusive right edge.</summary>
    public double Right => X + Width;

    /// <summary>Exclusive bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>True when the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Bounding rectangle of a set of points.</summary>
    public static RectD FromPoints(IEnumerable<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return any ? new RectD(minX, minY, maxX - minX, maxY - minY) : default;
    }

    /// <summary>Smallest rectangle containing both rectangles.</summary>
    public RectD Union(RectD other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        double l = Math.Min(X, other.X), t = Math.Min(Y, other.Y);
        return new RectD(l, t, Math.Max(Right, other.Right) - l, Math.Max(Bottom, other.Bottom) - t);
    }

    /// <summary>True when the two rectangles overlap with positive area.</summary>
    public bool IntersectsWith(RectD other) =>
        !IsEmpty && !other.IsEmpty && other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    /// <inheritdoc />
    public override string ToString() => $"[{X:0.##}, {Y:0.##}, {Width:0.##}x{Height:0.##}]";
}

/// <summary>Per-side distances, used for padding and expansion.</summary>
public readonly record struct Insets(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Zero on all sides.</summary>
    public static Insets Zero => default;

    /// <summary>The same value on every side.</summary>
    public static Insets Uniform(double value) => new(value, value, value, value);

    /// <summary>True when every side is zero.</summary>
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}
