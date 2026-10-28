namespace WindowVeil.Ocr.Paddle;

/// <summary>A rotated rectangle as returned by <c>cv2.minAreaRect</c> (float32 fields, angle in degrees).</summary>
internal readonly record struct RotatedRectF(float CenterX, float CenterY, float Width, float Height, float Angle)
{
    /// <summary><c>cv2.boxPoints</c>: the four corners (float32 arithmetic, same order as OpenCV).</summary>
    public (float X, float Y)[] Points()
    {
        double angle = Angle * Math.PI / 180.0;
        float b = (float)Math.Cos(angle) * 0.5f;
        float a = (float)Math.Sin(angle) * 0.5f;
        var p = new (float X, float Y)[4];
        p[0] = (CenterX - a * Height - b * Width, CenterY + b * Height - a * Width);
        p[1] = (CenterX + a * Height - b * Width, CenterY - b * Height - a * Width);
        p[2] = (2 * CenterX - p[0].X, 2 * CenterY - p[0].Y);
        p[3] = (2 * CenterX - p[1].X, 2 * CenterY - p[1].Y);
        return p;
    }
}

/// <summary>
/// Port of OpenCV's <c>cv::minAreaRect</c> (convex hull + rotating calipers, float32 arithmetic) and of PaddleOCR's
/// <c>get_mini_boxes</c> corner ordering.
/// </summary>
internal static class MinAreaRect
{
    /// <summary>
    /// Port of OpenCV's <c>cv::convexHull(points, hull, clockwise=false, returnPoints=true)</c> for integer points (Sklansky
    /// scan plus the index-based cyclic shift of the output). The start vertex depends on the input order, and it decides
    /// which of several equal-area rectangles the rotating calipers report, so it is reproduced exactly.
    /// </summary>
    /// <remarks>OpenCV sorts with the unstable <c>std::sort</c>; this port sorts stably, which can only differ for duplicate points.</remarks>
    public static List<(int X, int Y)> ConvexHull(IReadOnlyList<(int X, int Y)> data)
    {
        int total = data.Count;
        if (total == 0) return [];
        var pointer = Enumerable.Range(0, total).OrderBy(i => data[i].X).ThenBy(i => data[i].Y).ToArray();
        int minY = 0, maxY = 0;
        for (int i = 1; i < total; i++)
        {
            int y = data[pointer[i]].Y;
            if (data[pointer[minY]].Y > y) minY = i;
            if (data[pointer[maxY]].Y < y) maxY = i;
        }
        var hullbuf = new int[total];
        int nout = 0;
        if (data[pointer[0]] == data[pointer[total - 1]])
        {
            hullbuf[nout++] = 0;
        }
        else
        {
            var tl = new int[total + 2];
            var tr = new int[total + 2];
            int tlCount = Sklansky(data, pointer, 0, maxY, tl, -1, 1);
            int trCount = Sklansky(data, pointer, total - 1, maxY, tr, -1, -1);
            // clockwise == false
            (tl, tr) = (tr, tl);
            (tlCount, trCount) = (trCount, tlCount);
            for (int i = 0; i < tlCount - 1; i++) hullbuf[nout++] = pointer[tl[i]];
            for (int i = trCount - 1; i > 0; i--) hullbuf[nout++] = pointer[tr[i]];
            int stopIdx = trCount > 2 ? tr[1] : tlCount > 2 ? tl[tlCount - 2] : -1;

            var bl = new int[total + 2];
            var br = new int[total + 2];
            int blCount = Sklansky(data, pointer, 0, minY, bl, 1, -1);
            int brCount = Sklansky(data, pointer, total - 1, minY, br, 1, 1);
            if (stopIdx >= 0)
            {
                int checkIdx = blCount > 2 ? bl[1] : blCount + brCount > 2 ? br[2 - blCount] : -1;
                if (checkIdx == stopIdx || (checkIdx >= 0 && data[pointer[checkIdx]] == data[pointer[stopIdx]]))
                {
                    // All points on one line: the bottom part mirrors the top part.
                    blCount = Math.Min(blCount, 2);
                    brCount = Math.Min(brCount, 2);
                }
            }
            for (int i = 0; i < blCount - 1; i++) hullbuf[nout++] = pointer[bl[i]];
            for (int i = brCount - 1; i > 0; i--) hullbuf[nout++] = pointer[br[i]];

            // Cyclic shift so the hull indices form an ascending or descending sequence when possible.
            if (nout >= 3)
            {
                int minIdx = 0, maxIdx = 0, lt = 0;
                for (int i = 1; i < nout; i++)
                {
                    int idx = hullbuf[i];
                    lt += hullbuf[i - 1] < idx ? 1 : 0;
                    if (lt > 1 && lt < i) break;
                    if (idx < hullbuf[minIdx]) minIdx = i;
                    if (idx > hullbuf[maxIdx]) maxIdx = i;
                }
                int mmdist = Math.Abs(maxIdx - minIdx);
                if ((mmdist == 1 || mmdist == nout - 1) && (lt <= 1 || lt >= nout - 2))
                {
                    bool ascending = (maxIdx + 1) % nout == minIdx;
                    int i0 = ascending ? minIdx : maxIdx, j = i0;
                    if (i0 > 0)
                    {
                        var stack = new int[nout];
                        int i;
                        for (i = 0; i < nout; i++)
                        {
                            int curr = stack[i] = hullbuf[j];
                            int next = hullbuf[j + 1 < nout ? j + 1 : 0];
                            if (i < nout - 1 && ascending != (curr < next)) break;
                            j = j + 1 < nout ? j + 1 : 0;
                        }
                        if (i == nout) Array.Copy(stack, hullbuf, nout);
                    }
                }
            }
        }
        var hull = new List<(int X, int Y)>(nout);
        for (int i = 0; i < nout; i++) hull.Add(data[hullbuf[i]]);
        return hull;
    }

    /// <summary>OpenCV <c>Sklansky_</c> over sorted positions; returns the stack size.</summary>
    private static int Sklansky(IReadOnlyList<(int X, int Y)> data, int[] pointer, int start, int end, int[] stack, int nsign, int sign2)
    {
        (int X, int Y) P(int pos) => data[pointer[pos]];
        int incr = end > start ? 1 : -1;
        int pprev = start, pcur = pprev + incr, pnext = pcur + incr;
        int stacksize = 3;
        if (start == end || P(start) == P(end))
        {
            stack[0] = start;
            return 1;
        }
        stack[0] = pprev;
        stack[1] = pcur;
        stack[2] = pnext;
        end += incr;
        while (pnext != end)
        {
            int cury = P(pcur).Y, nexty = P(pnext).Y;
            int by = nexty - cury;
            if (Math.Sign(by) != nsign)
            {
                int ax = P(pcur).X - P(pprev).X;
                int bx = P(pnext).X - P(pcur).X;
                int ay = cury - P(pprev).Y;
                long convexity = (long)ay * bx - (long)ax * by;
                if (Math.Sign(convexity) == sign2 && (ax != 0 || ay != 0))
                {
                    pprev = pcur;
                    pcur = pnext;
                    pnext += incr;
                    stack[stacksize] = pnext;
                    stacksize++;
                }
                else if (pprev == start)
                {
                    pcur = pnext;
                    stack[1] = pcur;
                    pnext += incr;
                    stack[2] = pnext;
                }
                else
                {
                    stack[stacksize - 2] = pnext;
                    pcur = pprev;
                    pprev = stack[stacksize - 4];
                    stacksize--;
                }
            }
            else
            {
                pnext += incr;
                stack[stacksize - 1] = pnext;
            }
        }
        return --stacksize;
    }

    /// <summary><c>cv2.minAreaRect</c> for integer points.</summary>
    public static RotatedRectF Compute(IReadOnlyList<(int X, int Y)> points)
    {
        var hull = ConvexHull(points);
        var h = hull.Select(p => ((float)p.X, (float)p.Y)).ToArray();
        int n = h.Length;
        float cx = 0, cy = 0, w = 0, ht = 0, angle = 0;
        if (n > 2)
        {
            var o = RotatingCalipers(h);
            cx = o[0] + (o[2] + o[4]) * 0.5f;
            cy = o[1] + (o[3] + o[5]) * 0.5f;
            w = (float)Math.Sqrt((double)o[2] * o[2] + (double)o[3] * o[3]);
            ht = (float)Math.Sqrt((double)o[4] * o[4] + (double)o[5] * o[5]);
            angle = (float)Math.Atan2(o[3], (double)o[2]);
        }
        else if (n == 2)
        {
            cx = (h[0].Item1 + h[1].Item1) * 0.5f;
            cy = (h[0].Item2 + h[1].Item2) * 0.5f;
            double dx = h[1].Item1 - h[0].Item1, dy = h[1].Item2 - h[0].Item2;
            w = (float)Math.Sqrt(dx * dx + dy * dy);
            ht = 0;
            angle = (float)Math.Atan2(dy, dx);
        }
        else if (n == 1)
        {
            cx = h[0].Item1;
            cy = h[0].Item2;
        }
        angle = (float)(angle * 180 / Math.PI);
        return new RotatedRectF(cx, cy, w, ht, angle);
    }

    /// <summary>OpenCV <c>rotatingCalipers(..., CALIPERS_MINAREARECT)</c>: returns corner (x, y), side vector 1, side vector 2.</summary>
    private static float[] RotatingCalipers((float X, float Y)[] points)
    {
        int n = points.Length;
        var vect = new (float X, float Y)[n];
        var invLen = new float[n];
        float minArea = float.MaxValue;
        int left = 0, bottom = 0, right = 0, top = 0;
        var pt0 = points[0];
        float leftX = pt0.X, rightX = pt0.X, topY = pt0.Y, bottomY = pt0.Y;
        for (int i = 0; i < n; i++)
        {
            if (pt0.X < leftX) { leftX = pt0.X; left = i; }
            if (pt0.X > rightX) { rightX = pt0.X; right = i; }
            if (pt0.Y > topY) { topY = pt0.Y; top = i; }
            if (pt0.Y < bottomY) { bottomY = pt0.Y; bottom = i; }
            var pt = points[i + 1 < n ? i + 1 : 0];
            double dx = pt.X - pt0.X, dy = pt.Y - pt0.Y;
            vect[i] = ((float)dx, (float)dy);
            invLen[i] = (float)(1.0 / Math.Sqrt(dx * dx + dy * dy));
            pt0 = pt;
        }

        float orientation = 0;
        {
            double ax = vect[n - 1].X, ay = vect[n - 1].Y;
            for (int i = 0; i < n; i++)
            {
                double bx = vect[i].X, by = vect[i].Y;
                double convexity = ax * by - ay * bx;
                if (convexity != 0) { orientation = convexity > 0 ? 1f : -1f; break; }
                ax = bx; ay = by;
            }
            if (orientation == 0) throw new InvalidOperationException("Degenerate convex hull.");
        }
        float baseA = orientation, baseB = 0;
        Span<int> seq = [bottom, right, top, left];
        int bestLeft = 0, bestBottom = 0;
        float bestA = 0, bestB = 0, bestWidth = 0, bestHeight = 0;
        Span<float> dp = stackalloc float[4];
        for (int k = 0; k < n; k++)
        {
            dp[0] = +baseA * vect[seq[0]].X + baseB * vect[seq[0]].Y;
            dp[1] = -baseB * vect[seq[1]].X + baseA * vect[seq[1]].Y;
            dp[2] = -baseA * vect[seq[2]].X - baseB * vect[seq[2]].Y;
            dp[3] = +baseB * vect[seq[3]].X - baseA * vect[seq[3]].Y;
            int main = 0;
            float maxCos = dp[0] * invLen[seq[0]];
            for (int i = 1; i < 4; i++)
            {
                float cosAlpha = dp[i] * invLen[seq[i]];
                if (cosAlpha > maxCos) { main = i; maxCos = cosAlpha; }
            }
            int pindex = seq[main];
            float leadX = vect[pindex].X * invLen[pindex];
            float leadY = vect[pindex].Y * invLen[pindex];
            switch (main)
            {
                case 0: baseA = leadX; baseB = leadY; break;
                case 1: baseA = leadY; baseB = -leadX; break;
                case 2: baseA = -leadX; baseB = -leadY; break;
                default: baseA = -leadY; baseB = leadX; break;
            }
            seq[main] += 1;
            if (seq[main] == n) seq[main] = 0;

            float dxw = points[seq[1]].X - points[seq[3]].X;
            float dyw = points[seq[1]].Y - points[seq[3]].Y;
            float width = dxw * baseA + dyw * baseB;
            float dxh = points[seq[2]].X - points[seq[0]].X;
            float dyh = points[seq[2]].Y - points[seq[0]].Y;
            float height = -dxh * baseB + dyh * baseA;
            float area = width * height;
            if (area <= minArea)
            {
                minArea = area;
                bestLeft = seq[3];
                bestA = baseA;
                bestWidth = width;
                bestB = baseB;
                bestHeight = height;
                bestBottom = seq[0];
            }
        }

        float a1 = bestA, b1 = bestB, a2 = -bestB, b2 = bestA;
        float c1 = a1 * points[bestLeft].X + points[bestLeft].Y * b1;
        float c2 = a2 * points[bestBottom].X + points[bestBottom].Y * b2;
        float idet = 1f / (a1 * b2 - a2 * b1);
        float px = (c1 * b2 - c2 * b1) * idet;
        float py = (a1 * c2 - a2 * c1) * idet;
        return [px, py, a1 * bestWidth, b1 * bestWidth, a2 * bestHeight, b2 * bestHeight];
    }

    /// <summary>
    /// PaddleOCR <c>get_mini_boxes</c>: min-area rectangle corners sorted by x, then ordered top-left, top-right,
    /// bottom-right, bottom-left; also returns the shorter side.
    /// </summary>
    public static ((float X, float Y)[] Box, float ShortSide) GetMiniBox(IReadOnlyList<(int X, int Y)> points)
    {
        var rect = Compute(points);
        return (OrderCorners(rect.Points()), Math.Min(rect.Width, rect.Height));
    }

    /// <summary>The corner ordering shared by <c>get_mini_boxes</c> and <c>get_minarea_rect_crop</c>.</summary>
    public static (float X, float Y)[] OrderCorners((float X, float Y)[] corners)
    {
        // Python's sorted() is stable; OrderBy is stable as well.
        var p = corners.OrderBy(c => c.X).ToArray();
        int i1, i2, i3, i4;
        if (p[1].Y > p[0].Y) { i1 = 0; i4 = 1; } else { i1 = 1; i4 = 0; }
        if (p[3].Y > p[2].Y) { i2 = 2; i3 = 3; } else { i2 = 3; i3 = 2; }
        return [p[i1], p[i2], p[i3], p[i4]];
    }
}
