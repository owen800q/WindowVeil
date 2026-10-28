namespace WindowVeil.Ocr.Paddle;

/// <summary>A detected text quadrilateral (integer pixel coordinates of the original image) and its DB box score.</summary>
/// <param name="Points">Corners ordered top-left, top-right, bottom-right, bottom-left (PaddleOCR order).</param>
/// <param name="Score">Mean probability inside the un-expanded box (<c>box_score_fast</c>).</param>
public readonly record struct DetectedBox((int X, int Y)[] Points, double Score);

/// <summary>Parameters of <see cref="DbPostProcessor"/> (PaddleX <c>DBPostProcess</c>, <c>box_type="quad"</c>, <c>score_mode="fast"</c>).</summary>
/// <param name="Threshold">Binarization threshold on the probability map (<c>thresh</c>).</param>
/// <param name="BoxThreshold">Minimum mean probability inside a box (<c>box_thresh</c>).</param>
/// <param name="UnclipRatio">Expansion ratio (<c>unclip_ratio</c>): distance = area × ratio / perimeter.</param>
/// <param name="MaxCandidates">Maximum number of contours considered (<c>max_candidates</c>).</param>
public readonly record struct DbParameters(float Threshold, float BoxThreshold, double UnclipRatio, int MaxCandidates);

/// <summary>
/// Differentiable-Binarization post-processing: probability map → text quadrilaterals. Re-implements PaddleX
/// <c>DBPostProcess.boxes_from_bitmap</c>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Binarize: <c>pred &gt; thresh</c> (compared in float32, like NumPy).</item>
/// <item>Contours: <c>cv2.findContours(RETR_LIST, CHAIN_APPROX_SIMPLE)</c>. Components are found by labeling (8-connected
/// foreground for outer borders, enclosed 4-connected background for holes); each border is then traced with a port of
/// OpenCV's border-following routine so the vertices and their order match OpenCV (the order decides the convex hull's
/// start vertex and hence tie-breaking in the min-area rectangle). Contours are returned in OpenCV's order (reverse
/// raster order of discovery) so <c>max_candidates</c> truncates identically.</item>
/// <item><c>get_mini_boxes</c>: OpenCV min-area rectangle (port of the rotating-calipers code) with PaddleOCR's corner order;
/// boxes whose short side is below <c>min_size = 3</c> are dropped.</item>
/// <item><c>box_score_fast</c>: mean probability over the box polygon rasterized like <c>cv2.fillPoly</c>; boxes below
/// <c>box_thresh</c> are dropped.</item>
/// <item><c>unclip</c>: Clipper round-join offset by area × unclip_ratio / perimeter, then min-area rectangle again; short side
/// below <c>min_size + 2</c> is dropped.</item>
/// <item>Scale to the original image (float32 multiply, round half to even, clamp to [0, size]).</item>
/// </list>
/// </remarks>
public static class DbPostProcessor
{
    private const int MinSize = 3;

    /// <summary>Runs the post-processing on one probability map.</summary>
    /// <param name="pred">Probability map, row-major <paramref name="height"/> × <paramref name="width"/>.</param>
    /// <param name="width">Map width (resized image width).</param>
    /// <param name="height">Map height.</param>
    /// <param name="destWidth">Original image width (<c>src_w</c>).</param>
    /// <param name="destHeight">Original image height (<c>src_h</c>).</param>
    /// <param name="parameters">Thresholds.</param>
    public static IReadOnlyList<DetectedBox> Process(ReadOnlySpan<float> pred, int width, int height, int destWidth, int destHeight, DbParameters parameters)
    {
        if (pred.Length < width * height) throw new ArgumentException("Probability map is smaller than width × height.", nameof(pred));
        var bitmap = new bool[width * height];
        for (int i = 0; i < bitmap.Length; i++) bitmap[i] = pred[i] > parameters.Threshold;

        var contours = FindContours(bitmap, width, height);
        float widthScale = (float)((double)destWidth / width), heightScale = (float)((double)destHeight / height);
        var boxes = new List<DetectedBox>();
        int count = Math.Min(contours.Count, parameters.MaxCandidates);
        for (int c = 0; c < count; c++)
        {
            var (points, sside) = MinAreaRect.GetMiniBox(contours[c]);
            if (sside < MinSize) continue;
            double score = BoxScoreFast(pred, width, height, points);
            if (parameters.BoxThreshold > score) continue;

            var expanded = Unclip(points, parameters.UnclipRatio);
            if (expanded.Count == 0) continue;
            var (box, sside2) = MinAreaRect.GetMiniBox(expanded.Select(p => ((int)p.X, (int)p.Y)).ToArray());
            if (sside2 < MinSize + 2) continue;

            var result = new (int X, int Y)[4];
            for (int i = 0; i < 4; i++)
            {
                double x = Math.Round(box[i].X * widthScale, MidpointRounding.ToEven);
                double y = Math.Round(box[i].Y * heightScale, MidpointRounding.ToEven);
                result[i] = ((int)Math.Max(0, Math.Min(x, destWidth)), (int)Math.Max(0, Math.Min(y, destHeight)));
            }
            boxes.Add(new DetectedBox(result, score));
        }
        return boxes;
    }

    /// <summary><c>DBPostProcess.unclip</c>: offset polygon (integer vertices).</summary>
    internal static List<(long X, long Y)> Unclip(IReadOnlyList<(float X, float Y)> box, double unclipRatio)
    {
        double area = ContourArea(box);
        double length = ArcLength(box);
        if (length <= 0) return [];
        double distance = area * unclipRatio / length;
        return ClipperOffset.OffsetRound(box, distance);
    }

    /// <summary><c>cv2.contourArea</c> for float points.</summary>
    internal static double ContourArea(IReadOnlyList<(float X, float Y)> pts)
    {
        double a = 0;
        var prev = pts[^1];
        foreach (var p in pts)
        {
            a += (double)prev.X * p.Y - (double)prev.Y * p.X;
            prev = p;
        }
        return Math.Abs(a * 0.5);
    }

    /// <summary><c>cv2.arcLength(closed=True)</c> for float points (float32 deltas, double accumulator).</summary>
    internal static double ArcLength(IReadOnlyList<(float X, float Y)> pts)
    {
        double perimeter = 0;
        var prev = pts[^1];
        foreach (var p in pts)
        {
            float dx = p.X - prev.X, dy = p.Y - prev.Y;
            perimeter += MathF.Sqrt(dx * dx + dy * dy);
            prev = p;
        }
        return perimeter;
    }

    /// <summary><c>box_score_fast</c>: mean of <paramref name="pred"/> over the polygon, inside its clipped bounding box.</summary>
    internal static double BoxScoreFast(ReadOnlySpan<float> pred, int w, int h, (float X, float Y)[] box)
    {
        float minX = box.Min(p => p.X), maxX = box.Max(p => p.X), minY = box.Min(p => p.Y), maxY = box.Max(p => p.Y);
        int xmin = Math.Clamp((int)Math.Floor(minX), 0, w - 1), xmax = Math.Clamp((int)Math.Ceiling(maxX), 0, w - 1);
        int ymin = Math.Clamp((int)Math.Floor(minY), 0, h - 1), ymax = Math.Clamp((int)Math.Ceiling(maxY), 0, h - 1);
        int mw = xmax - xmin + 1, mh = ymax - ymin + 1;
        // (box - [xmin, ymin]).astype(np.int32): float32 subtraction, truncation toward zero.
        var poly = box.Select(p => ((int)(p.X - xmin), (int)(p.Y - ymin))).ToArray();
        var mask = PolygonRasterizer.FillPoly(poly, mw, mh);
        double sum = 0;
        int n = 0;
        for (int y = 0; y < mh; y++)
        {
            int row = (ymin + y) * w + xmin;
            for (int x = 0; x < mw; x++)
            {
                if (!mask[y * mw + x]) continue;
                sum += pred[row + x];
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>
    /// The contours <c>cv2.findContours(binary, RETR_LIST, CHAIN_APPROX_SIMPLE)</c> returns, in the same order, with the
    /// same vertices in the same order.
    /// </summary>
    internal static List<List<(int X, int Y)>> FindContours(bool[] bitmap, int w, int h)
    {
        var found = new List<(long Key, List<(int X, int Y)> Points)>();
        var visited = new bool[w * h];
        var stack = new int[w * h]; // every pixel is pushed at most once per pass
        // Zero-padded copy for border following (OpenCV pads the image with one zero pixel on every side).
        int pw = w + 2;
        var padded = new bool[pw * (h + 2)];
        for (int y = 0; y < h; y++) Array.Copy(bitmap, y * w, padded, (y + 1) * pw + 1, w);

        // Outer borders: 8-connected foreground components.
        for (int start = 0; start < bitmap.Length; start++)
        {
            if (!bitmap[start] || visited[start]) continue;
            int top = 0;
            visited[start] = true;
            stack[top++] = start;
            while (top > 0)
            {
                int p = stack[--top];
                int px = p % w, py = p / w;
                int y0 = py > 0 ? -1 : 0, y1 = py < h - 1 ? 1 : 0, x0 = px > 0 ? -1 : 0, x1 = px < w - 1 ? 1 : 0;
                for (int dy = y0; dy <= y1; dy++)
                {
                    int rowBase = p + dy * w;
                    for (int dx = x0; dx <= x1; dx++)
                    {
                        int q = rowBase + dx;
                        if (bitmap[q] && !visited[q]) { visited[q] = true; stack[top++] = q; }
                    }
                }
            }
            found.Add(((long)start * 2, TraceBorder(padded, pw, start % w, start / w, isHole: false)));
        }

        // Hole borders: 4-connected background components that do not touch the image border. "visited" is reused: it is
        // true for every foreground pixel now, and background pixels are marked as they are flooded.
        for (int start = 0; start < bitmap.Length; start++)
        {
            if (visited[start]) continue;
            bool touchesBorder = false;
            int top = 0;
            visited[start] = true;
            stack[top++] = start;
            while (top > 0)
            {
                int p = stack[--top];
                int px = p % w, py = p / w;
                if (px == 0 || py == 0 || px == w - 1 || py == h - 1) touchesBorder = true;
                if (px > 0 && !visited[p - 1]) { visited[p - 1] = true; stack[top++] = p - 1; }
                if (px < w - 1 && !visited[p + 1]) { visited[p + 1] = true; stack[top++] = p + 1; }
                if (py > 0 && !visited[p - w]) { visited[p - w] = true; stack[top++] = p - w; }
                if (py < h - 1 && !visited[p + w]) { visited[p + w] = true; stack[top++] = p + w; }
            }
            if (touchesBorder) continue;
            // Discovered by the raster scan at the foreground pixel just left of the hole's first pixel, after that pixel's
            // own outer border (if it starts there), hence the +1. Interior holes never start in column 0.
            int origin = start - 1;
            found.Add(((long)origin * 2 + 1, TraceBorder(padded, pw, origin % w, origin / w, isHole: true)));
        }

        // OpenCV returns contours in reverse order of discovery.
        return found.OrderByDescending(f => f.Key).Select(f => f.Points).ToList();
    }

    private static readonly (int X, int Y)[] CodeDeltas = [(1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1)];

    /// <summary>
    /// OpenCV <c>icvFetchContour</c> border following (8-connectivity) with <c>CHAIN_APPROX_SIMPLE</c>: returns the contour
    /// vertices in OpenCV's order, starting at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    private static List<(int X, int Y)> TraceBorder(bool[] padded, int pw, int x, int y, bool isHole)
    {
        Span<int> deltas = stackalloc int[16];
        deltas[0] = 1; deltas[1] = -pw + 1; deltas[2] = -pw; deltas[3] = -pw - 1;
        deltas[4] = -1; deltas[5] = pw - 1; deltas[6] = pw; deltas[7] = pw + 1;
        for (int k = 0; k < 8; k++) deltas[k + 8] = deltas[k];

        var pts = new List<(int X, int Y)>();
        var pt = (X: x, Y: y);
        int i0 = (y + 1) * pw + x + 1, i1, i3, i4 = 0;
        int s, sEnd;
        sEnd = s = isHole ? 0 : 4;
        do
        {
            s = (s - 1) & 7;
            i1 = i0 + deltas[s];
        }
        while (!padded[i1] && s != sEnd);

        if (s == sEnd)
        {
            pts.Add(pt); // single-pixel component
            return pts;
        }

        i3 = i0;
        int prevS = s ^ 4;
        for (int guard = 0; guard < padded.Length * 4; guard++)
        {
            sEnd = s;
            s = Math.Min(s, 15);
            while (s < 15)
            {
                i4 = i3 + deltas[++s];
                if (padded[i4]) break;
            }
            s &= 7;
            if (s != prevS)
            {
                pts.Add(pt);
                prevS = s;
            }
            pt = (pt.X + CodeDeltas[s].X, pt.Y + CodeDeltas[s].Y);
            if (i4 == i0 && i3 == i1) break;
            i3 = i4;
            s = (s + 4) & 7;
        }
        return pts;
    }
}

/// <summary>Rasterizes polygons like <c>cv2.fillPoly(mask, [pts], 1)</c> with integer vertices and LINE_8 edges.</summary>
internal static class PolygonRasterizer
{
    private const int XyShift = 16;
    private const long XyOne = 1L << XyShift;

    public static bool[] FillPoly((int X, int Y)[] pts, int w, int h)
    {
        var mask = new bool[w * h];
        int n = pts.Length;
        var edges = new List<(int Y0, int Y1, long X, long Dx)>();
        var p0 = pts[n - 1];
        for (int i = 0; i < n; i++)
        {
            var p1 = pts[i];
            DrawLine(mask, w, h, p0, p1);
            if (p0.Y != p1.Y)
            {
                long x0 = (long)p0.X << XyShift, x1 = (long)p1.X << XyShift;
                long dx = (x1 - x0) / (p1.Y - p0.Y);
                edges.Add(p0.Y < p1.Y ? (p0.Y, p1.Y, x0, dx) : (p1.Y, p0.Y, x1, dx));
            }
            p0 = p1;
        }
        if (edges.Count < 2) return mask;
        int yMin = edges.Min(e => e.Y0), yMax = edges.Max(e => e.Y1);
        var xs = new List<long>(edges.Count);
        for (int y = Math.Max(yMin, 0); y < Math.Min(yMax, h); y++)
        {
            xs.Clear();
            foreach (var e in edges)
                if (e.Y0 <= y && y < e.Y1) xs.Add(e.X + (y - e.Y0) * e.Dx);
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int xa = (int)((xs[i] + XyOne - 1) >> XyShift), xb = (int)(xs[i + 1] >> XyShift);
                if (xa >= w || xb < 0) continue;
                xa = Math.Max(xa, 0);
                xb = Math.Min(xb, w - 1);
                for (int x = xa; x <= xb; x++) mask[y * w + x] = true;
            }
        }
        return mask;
    }

    /// <summary>OpenCV <c>LineIterator</c> (8-connected, left to right) without clipping; out-of-range pixels are skipped.</summary>
    private static void DrawLine(bool[] mask, int w, int h, (int X, int Y) a, (int X, int Y) b)
    {
        if (b.X < a.X) (a, b) = (b, a);
        int dx = b.X - a.X, dy = b.Y - a.Y;
        int sy = dy < 0 ? -1 : 1;
        dy = Math.Abs(dy);
        bool steep = dy > dx;
        int major = steep ? dy : dx, minor = steep ? dx : dy;
        int err = major - (minor + minor), plusDelta = major + major, minusDelta = -(minor + minor);
        int x = a.X, y = a.Y;
        for (int i = 0; i <= major; i++)
        {
            if ((uint)x < (uint)w && (uint)y < (uint)h) mask[y * w + x] = true;
            bool m = err < 0;
            err += minusDelta + (m ? plusDelta : 0);
            // minusStep moves along the major axis, plusStep along the minor axis.
            if (steep) { y += sy; if (m) x += 1; }
            else { x += 1; if (m) y += sy; }
        }
    }
}
