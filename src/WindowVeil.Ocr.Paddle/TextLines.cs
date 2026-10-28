namespace WindowVeil.Ocr.Paddle;

/// <summary>PaddleX <c>SortQuadBoxes</c>: top-to-bottom, then left-to-right with a 10-pixel same-line tolerance.</summary>
public static class BoxSorter
{
    /// <summary>Sorts boxes in reading order exactly like the official pipeline (stable sort by the first corner, then a bubble pass).</summary>
    public static IReadOnlyList<DetectedBox> Sort(IReadOnlyList<DetectedBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        // sorted(dt_boxes, key=lambda x: (x[0][1], x[0][0])) is stable; OrderBy/ThenBy are stable too.
        var b = boxes.OrderBy(x => x.Points[0].Y).ThenBy(x => x.Points[0].X).ToList();
        for (int i = 0; i < b.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                if (Math.Abs(b[j + 1].Points[0].Y - b[j].Points[0].Y) < 10 && b[j + 1].Points[0].X < b[j].Points[0].X)
                    (b[j], b[j + 1]) = (b[j + 1], b[j]);
                else
                    break;
            }
        }
        return b;
    }
}

/// <summary>
/// PaddleX <c>CropByPolys.get_minarea_rect_crop</c> (the default for <c>det_box_type="quad"</c>): min-area rectangle of the
/// box, perspective warp to an axis-aligned patch (bicubic, replicated border), rotated 90° counter-clockwise when the
/// patch is at least 1.5 times taller than wide.
/// </summary>
internal static class TextLineCropper
{
    public static BgrImage? Crop(BgrImage image, (int X, int Y)[] box)
    {
        var rect = MinAreaRect.Compute(box);
        var p = MinAreaRect.OrderCorners(rect.Points());
        // int(max(np.linalg.norm(p0 - p1), np.linalg.norm(p2 - p3))) in float32.
        int cropWidth = (int)Math.Max(Norm(p[0], p[1]), Norm(p[2], p[3]));
        int cropHeight = (int)Math.Max(Norm(p[0], p[3]), Norm(p[1], p[2]));
        // OpenCV would treat a zero dimension as "same size as the source"; the official pipeline then discards empty crops.
        // A zero-size crop cannot hold text, so it is skipped here.
        if (cropWidth <= 0 || cropHeight <= 0) return null;
        (float X, float Y)[] dst = [(0, 0), (cropWidth, 0), (cropWidth, cropHeight), (0, cropHeight)];
        var m = ImageOps.GetPerspectiveTransform(p, dst);
        var crop = ImageOps.WarpPerspectiveCubicReplicate(image, m, cropWidth, cropHeight);
        if (crop.Height * 1.0 / crop.Width >= 1.5) crop = crop.Rot90();
        return crop;
    }

    private static float Norm((float X, float Y) a, (float X, float Y) b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
