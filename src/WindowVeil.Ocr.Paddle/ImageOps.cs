using WindowVeil.Imaging;

namespace WindowVeil.Ocr.Paddle;

/// <summary>
/// A tightly packed 8-bit, 3-channel image in B, G, R order (HWC), the layout the official PaddleOCR pipeline works on
/// (OpenCV <c>imread</c> returns BGR).
/// </summary>
internal sealed class BgrImage
{
    public BgrImage(int width, int height, byte[]? data = null)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must not be negative.");
        Width = width;
        Height = height;
        Data = data ?? new byte[checked(width * height * 3)];
        if (Data.Length < width * height * 3) throw new ArgumentException("Buffer too small.", nameof(data));
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>Converts a BGRA image (alpha is dropped, like <c>cv2.imread(..., IMREAD_COLOR)</c>).</summary>
    public static BgrImage FromBgra(BgraImage image, Geometry.PixelRect? region = null)
    {
        var r = region ?? new Geometry.PixelRect(0, 0, image.Width, image.Height);
        var dst = new BgrImage(r.Width, r.Height);
        var src = image.Pixels;
        var d = dst.Data;
        int di = 0;
        for (int y = 0; y < r.Height; y++)
        {
            int si = (r.Y + y) * image.Stride + r.X * 4;
            for (int x = 0; x < r.Width; x++, si += 4, di += 3)
            {
                d[di] = src[si];
                d[di + 1] = src[si + 1];
                d[di + 2] = src[si + 2];
            }
        }
        return dst;
    }

    /// <summary>Rotates 90° counter-clockwise, like <c>np.rot90(img)</c>.</summary>
    public BgrImage Rot90()
    {
        var dst = new BgrImage(Height, Width);
        for (int i = 0; i < dst.Height; i++)
        {
            int srcX = Width - 1 - i;
            for (int j = 0; j < dst.Width; j++)
            {
                int s = (j * Width + srcX) * 3, t = (i * dst.Width + j) * 3;
                dst.Data[t] = Data[s];
                dst.Data[t + 1] = Data[s + 1];
                dst.Data[t + 2] = Data[s + 2];
            }
        }
        return dst;
    }

    /// <summary>Pads with zeros on the right/bottom to at least the given size (official <c>DetResizeForTest.image_padding</c>).</summary>
    public BgrImage PadTo(int width, int height)
    {
        if (width <= Width && height <= Height) return this;
        var dst = new BgrImage(Math.Max(width, Width), Math.Max(height, Height));
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(Data, y * Width * 3, dst.Data, y * dst.Width * 3, Width * 3);
        return dst;
    }
}

/// <summary>
/// Bit-exact re-implementations of the OpenCV 4.x routines used by the official pipeline for 8-bit 3-channel images:
/// <c>cv2.resize(INTER_LINEAR)</c> and <c>cv2.warpPerspective(INTER_CUBIC, BORDER_REPLICATE)</c>, plus
/// <c>cv2.getPerspectiveTransform</c>. Verified against OpenCV output in the unit tests (Fixtures/vectors.json).
/// </summary>
internal static class ImageOps
{
    private const int ResizeCoefBits = 11; // INTER_RESIZE_COEF_BITS
    private const int ResizeCoefScale = 1 << ResizeCoefBits;
    private const int InterBits = 5; // INTER_BITS
    private const int InterTabSize = 1 << InterBits;
    private const int RemapCoefBits = 15; // INTER_REMAP_COEF_BITS
    private const int RemapCoefScale = 1 << RemapCoefBits;
    private const double DblEpsilon = 2.220446049250313e-16; // C DBL_EPSILON (not System.Double.Epsilon)

    private static readonly Lazy<short[]> CubicTab = new(BuildCubicTable);

    /// <summary><c>cv2.resize(src, (width, height))</c> with the default INTER_LINEAR interpolation (fixed-point path for 8U).</summary>
    public static BgrImage ResizeLinear(BgrImage src, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (src.Width == 0 || src.Height == 0) throw new ArgumentException("Source image is empty.", nameof(src));
        if (width == src.Width && height == src.Height) return new BgrImage(width, height, (byte[])src.Data.Clone());

        double invX = (double)width / src.Width, invY = (double)height / src.Height;
        double scaleX = 1.0 / invX, scaleY = 1.0 / invY;
        int iScaleX = (int)Math.Round(scaleX, MidpointRounding.ToEven), iScaleY = (int)Math.Round(scaleY, MidpointRounding.ToEven);
        bool areaFast = Math.Abs(scaleX - iScaleX) < DblEpsilon && Math.Abs(scaleY - iScaleY) < DblEpsilon;
        if (areaFast && iScaleX == 2 && iScaleY == 2) return ResizeAreaFast2x(src, width, height);

        const int cn = 3;
        int sw = src.Width, sh = src.Height;
        var xofs = new int[width];
        var alpha = new short[width * 2];
        for (int dx = 0; dx < width; dx++)
        {
            float fx = (float)((dx + 0.5) * scaleX - 0.5);
            int sx = (int)Math.Floor(fx);
            fx -= sx;
            if (sx < 0) { fx = 0; sx = 0; }
            if (sx >= sw - 1) { fx = 0; sx = sw - 1; }
            xofs[dx] = sx * cn;
            alpha[dx * 2] = SaturateShort((1f - fx) * ResizeCoefScale);
            alpha[dx * 2 + 1] = SaturateShort(fx * ResizeCoefScale);
        }

        // Horizontal pass for every source row, then the vertical pass. Both are row-parallel for large images.
        int rowLen = width * cn;
        var hrows = new int[sh][];
        var s = src.Data;
        bool parallel = (long)Math.Max(sw * sh, width * height) >= ParallelPixelThreshold;
        ForRows(sh, parallel, y =>
        {
            var row = new int[rowLen];
            int baseIdx = y * sw * cn;
            for (int dx = 0; dx < width; dx++)
            {
                int sx = xofs[dx];
                int a0 = alpha[dx * 2], a1 = alpha[dx * 2 + 1];
                int sx1 = Math.Min(sx + cn, (sw - 1) * cn);
                for (int k = 0; k < cn; k++)
                    row[dx * cn + k] = s[baseIdx + sx + k] * a0 + s[baseIdx + sx1 + k] * a1;
            }
            hrows[y] = row;
        });

        var dst = new BgrImage(width, height);
        var d = dst.Data;
        ForRows(height, parallel, dy =>
        {
            float fy = (float)((dy + 0.5) * scaleY - 0.5);
            int sy = (int)Math.Floor(fy);
            fy -= sy;
            int b0 = SaturateShort((1f - fy) * ResizeCoefScale), b1 = SaturateShort(fy * ResizeCoefScale);
            var r0 = hrows[Math.Clamp(sy, 0, sh - 1)];
            var r1 = hrows[Math.Clamp(sy + 1, 0, sh - 1)];
            int o = dy * rowLen;
            for (int x = 0; x < rowLen; x++)
                d[o + x] = (byte)Math.Clamp((((b0 * (r0[x] >> 4)) >> 16) + ((b1 * (r1[x] >> 4)) >> 16) + 2) >> 2, 0, 255);
        });
        return dst;
    }

    /// <summary>Images at least this large are processed with row-parallel loops.</summary>
    internal const long ParallelPixelThreshold = 256 * 1024;

    /// <summary>Runs <paramref name="body"/> for every row, in parallel when requested.</summary>
    internal static void ForRows(int rows, bool parallel, Action<int> body)
    {
        if (parallel) Parallel.For(0, rows, body);
        else for (int y = 0; y < rows; y++) body(y);
    }

    /// <summary>OpenCV switches INTER_LINEAR to the fast INTER_AREA path for exact 2× downscaling.</summary>
    private static BgrImage ResizeAreaFast2x(BgrImage src, int width, int height)
    {
        var dst = new BgrImage(width, height);
        int sw = src.Width;
        for (int y = 0; y < height; y++)
        {
            int r0 = (2 * y) * sw * 3, r1 = (2 * y + 1) * sw * 3;
            for (int x = 0; x < width; x++)
            {
                int i = 2 * x * 3;
                for (int k = 0; k < 3; k++)
                {
                    int sum = src.Data[r0 + i + k] + src.Data[r0 + i + 3 + k] + src.Data[r1 + i + k] + src.Data[r1 + i + 3 + k];
                    dst.Data[(y * width + x) * 3 + k] = (byte)((sum + 2) >> 2);
                }
            }
        }
        return dst;
    }

    /// <summary>
    /// <c>cv2.getPerspectiveTransform(src, dst)</c>: the 3×3 homography mapping four source points to four destination points
    /// (row-major, M[8] = 1).
    /// </summary>
    public static double[] GetPerspectiveTransform(ReadOnlySpan<(float X, float Y)> src, ReadOnlySpan<(float X, float Y)> dst)
    {
        if (src.Length != 4 || dst.Length != 4) throw new ArgumentException("Four point pairs are required.");
        var a = new double[8, 8];
        var b = new double[8];
        for (int i = 0; i < 4; i++)
        {
            a[i, 0] = a[i + 4, 3] = src[i].X;
            a[i, 1] = a[i + 4, 4] = src[i].Y;
            a[i, 2] = a[i + 4, 5] = 1;
            a[i, 3] = a[i, 4] = a[i, 5] = a[i + 4, 0] = a[i + 4, 1] = a[i + 4, 2] = 0;
            a[i, 6] = -src[i].X * (double)dst[i].X;
            a[i, 7] = -src[i].Y * (double)dst[i].X;
            a[i + 4, 6] = -src[i].X * (double)dst[i].Y;
            a[i + 4, 7] = -src[i].Y * (double)dst[i].Y;
            b[i] = dst[i].X;
            b[i + 4] = dst[i].Y;
        }
        var x = SolveLu(a, b) ?? throw new InvalidOperationException("Degenerate perspective transform.");
        return [x[0], x[1], x[2], x[3], x[4], x[5], x[6], x[7], 1.0];
    }

    /// <summary>Inverts a 3×3 matrix (row-major). Returns null when singular.</summary>
    public static double[]? Invert3x3(double[] m)
    {
        double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-300) return null;
        double inv = 1.0 / det;
        return
        [
            (m[4] * m[8] - m[5] * m[7]) * inv, (m[2] * m[7] - m[1] * m[8]) * inv, (m[1] * m[5] - m[2] * m[4]) * inv,
            (m[5] * m[6] - m[3] * m[8]) * inv, (m[0] * m[8] - m[2] * m[6]) * inv, (m[2] * m[3] - m[0] * m[5]) * inv,
            (m[3] * m[7] - m[4] * m[6]) * inv, (m[1] * m[6] - m[0] * m[7]) * inv, (m[0] * m[4] - m[1] * m[3]) * inv,
        ];
    }

    /// <summary>
    /// <c>cv2.warpPerspective(src, M, (width, height), flags=INTER_CUBIC, borderMode=BORDER_REPLICATE)</c>, including OpenCV's
    /// 1/32-pixel coordinate quantization and 15-bit fixed-point bicubic weights.
    /// </summary>
    public static BgrImage WarpPerspectiveCubicReplicate(BgrImage src, double[] forward, int width, int height)
    {
        var m = Invert3x3(forward) ?? throw new InvalidOperationException("The perspective transform is singular.");
        var tab = CubicTab.Value;
        var dst = new BgrImage(width, height);
        var s = src.Data;
        var d = dst.Data;
        int sw = src.Width, sh = src.Height, stride = sw * 3;
        Span<int> xs = stackalloc int[4];
        Span<int> ys = stackalloc int[4];
        // OpenCV evaluates the map in blocks of about 32x32 pixels: X0 = M0*xBlock + M1*y + M2, then X0 + M0*x1 inside the
        // block. Reproducing the block origin keeps the floating-point rounding (and the 1/32-pixel quantization) identical.
        int bh0 = Math.Min(16, height);
        int bw0 = Math.Min(1024 / bh0, width);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int xb = x - x % bw0, x1 = x - xb;
                double x0 = m[0] * xb + m[1] * y + m[2], y0 = m[3] * xb + m[4] * y + m[5], w0 = m[6] * xb + m[7] * y + m[8];
                double w = w0 + m[6] * x1;
                w = w != 0 ? InterTabSize / w : 0;
                double fX = Math.Max(int.MinValue, Math.Min(int.MaxValue, (x0 + m[0] * x1) * w));
                double fY = Math.Max(int.MinValue, Math.Min(int.MaxValue, (y0 + m[3] * x1) * w));
                int ix = (int)Math.Round(fX, MidpointRounding.ToEven), iy = (int)Math.Round(fY, MidpointRounding.ToEven);
                int sx = SaturateShort(ix >> InterBits) - 1, sy = SaturateShort(iy >> InterBits) - 1;
                int wi = ((iy & (InterTabSize - 1)) * InterTabSize + (ix & (InterTabSize - 1))) * 16;
                for (int i = 0; i < 4; i++)
                {
                    xs[i] = Math.Clamp(sx + i, 0, sw - 1) * 3;
                    ys[i] = Math.Clamp(sy + i, 0, sh - 1) * stride;
                }
                int o = (y * width + x) * 3;
                for (int k = 0; k < 3; k++)
                {
                    int sum = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        int row = ys[i] + k;
                        sum += s[row + xs[0]] * tab[wi + i * 4] + s[row + xs[1]] * tab[wi + i * 4 + 1]
                             + s[row + xs[2]] * tab[wi + i * 4 + 2] + s[row + xs[3]] * tab[wi + i * 4 + 3];
                    }
                    d[o + k] = (byte)Math.Clamp((sum + (1 << (RemapCoefBits - 1))) >> RemapCoefBits, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>OpenCV <c>initInterTab2D(INTER_CUBIC, fixpt=true)</c>: 32×32 tables of 4×4 weights summing to 2^15.</summary>
    private static short[] BuildCubicTable()
    {
        var tab1 = new float[InterTabSize * 4];
        float scale = 1f / InterTabSize;
        for (int i = 0; i < InterTabSize; i++) InterpolateCubic(i * scale, tab1.AsSpan(i * 4, 4));
        var itab = new short[InterTabSize * InterTabSize * 16];
        for (int i = 0; i < InterTabSize; i++)
        {
            for (int j = 0; j < InterTabSize; j++)
            {
                var t = itab.AsSpan((i * InterTabSize + j) * 16, 16);
                int isum = 0;
                for (int k1 = 0; k1 < 4; k1++)
                {
                    float vy = tab1[i * 4 + k1];
                    for (int k2 = 0; k2 < 4; k2++)
                    {
                        float v = vy * tab1[j * 4 + k2];
                        t[k1 * 4 + k2] = SaturateShort(v * RemapCoefScale);
                        isum += t[k1 * 4 + k2];
                    }
                }
                if (isum != RemapCoefScale)
                {
                    int diff = isum - RemapCoefScale;
                    int mk1 = 2, mk2 = 2, bigK1 = 2, bigK2 = 2;
                    for (int k1 = 2; k1 < 4; k1++)
                    {
                        for (int k2 = 2; k2 < 4; k2++)
                        {
                            if (t[k1 * 4 + k2] < t[mk1 * 4 + mk2]) { mk1 = k1; mk2 = k2; }
                            else if (t[k1 * 4 + k2] > t[bigK1 * 4 + bigK2]) { bigK1 = k1; bigK2 = k2; }
                        }
                    }
                    if (diff < 0) t[bigK1 * 4 + bigK2] = (short)(t[bigK1 * 4 + bigK2] - diff);
                    else t[mk1 * 4 + mk2] = (short)(t[mk1 * 4 + mk2] - diff);
                }
            }
        }
        return itab;
    }

    private static void InterpolateCubic(float x, Span<float> c)
    {
        const float A = -0.75f;
        c[0] = ((A * (x + 1) - 5 * A) * (x + 1) + 8 * A) * (x + 1) - 4 * A;
        c[1] = ((A + 2) * x - (A + 3)) * x * x + 1;
        c[2] = ((A + 2) * (1 - x) - (A + 3)) * (1 - x) * (1 - x) + 1;
        c[3] = 1f - c[0] - c[1] - c[2];
    }

    /// <summary>OpenCV <c>saturate_cast&lt;short&gt;</c> of a float/int (round half to even, then clamp).</summary>
    private static short SaturateShort(double v)
    {
        double r = Math.Round(v, MidpointRounding.ToEven);
        return (short)Math.Clamp(r, short.MinValue, short.MaxValue);
    }

    private static double[]? SolveLu(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < double.Epsilon) return null;
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                (x[col], x[piv]) = (x[piv], x[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = x[r];
            for (int c = r + 1; c < n; c++) sum -= m[r, c] * x[c];
            x[r] = sum / m[r, r];
        }
        return x;
    }
}
