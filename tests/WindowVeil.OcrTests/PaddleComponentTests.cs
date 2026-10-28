using WindowVeil.Ocr.Paddle;
using Xunit;

namespace WindowVeil.OcrTests;

/// <summary>Unit tests of the Paddle pipeline pieces on synthetic inputs (no models needed).</summary>
public class PaddleComponentTests
{
    [Theory]
    [InlineData(520, 100, 64, DetectionLimitType.Min, 4000, 512, 96)]   // no scaling, rounded to multiples of 32
    [InlineData(1920, 1080, 64, DetectionLimitType.Min, 4000, 1920, 1088)]
    [InlineData(300, 40, 64, DetectionLimitType.Min, 4000, 480, 64)]    // short side upscaled to 64
    [InlineData(1920, 1080, 960, DetectionLimitType.Max, 4000, 960, 544)]
    [InlineData(8000, 1000, 64, DetectionLimitType.Min, 4000, 4000, 512)] // max_side_limit
    [InlineData(10, 20, 64, DetectionLimitType.Min, 4000, 64, 64)]      // h + w < 64: padded to 32×32 first
    public void DetectionResize_ProducesMultiplesOf32WithinLimits(int w, int h, int limit, DetectionLimitType type, int maxSide, int ew, int eh)
    {
        var r = DetectionPreprocessor.ComputeResize(w, h, limit, type, maxSide);
        Assert.Equal((ew, eh), (r.Width, r.Height));
        Assert.Equal(0, r.Width % 32);
        Assert.Equal(0, r.Height % 32);
        Assert.True(Math.Max(r.Width, r.Height) <= maxSide + 16);
    }

    [Fact]
    public void DetectionNormalization_UsesMeanStdPerBgrChannel()
    {
        var img = new BgrImage(1, 1, [255, 0, 128]); // B=255, G=0, R=128
        var t = DetectionPreprocessor.Normalize(img, DetectionNormalization.ImageNet);
        Assert.Equal((1 - 0.485) / 0.229, t[0], 5); // channel 0 = blue uses mean[0]/std[0] (official BGR behavior)
        Assert.Equal(-0.456 / 0.224, t[1], 5);
        Assert.Equal((128 / 255.0 - 0.406) / 0.225, t[2], 5);
    }

    [Theory]
    [InlineData(100, 20, 240, 320)]   // ratio 5 → resized to 240, padded to the nominal 320
    [InlineData(400, 20, 960, 960)]   // ratio 20 → wider than 320: width int(48 × 20)
    [InlineData(10, 30, 16, 320)]     // tall crop: ceil(48 / 3)
    [InlineData(2000, 10, 3200, 3200)] // capped at max_imgW
    public void RecognitionResize_KeepsAspectAndPadsRight(int w, int h, int resized, int padded)
    {
        Assert.Equal((resized, padded), RecognitionPreprocessor.ComputeWidths(w, h, 48, 320));
    }

    [Fact]
    public void RecognitionBatch_NormalizesToMinusOneOneAndZeroPads()
    {
        var white = new BgrImage(10, 48, Enumerable.Repeat((byte)255, 10 * 48 * 3).ToArray());
        var (data, width) = RecognitionPreprocessor.PrepareBatch([white], 48, 320);
        Assert.Equal(320, width);
        Assert.Equal(1f, data[0]);          // (255/255 - 0.5) / 0.5
        Assert.Equal(1f, data[9]);
        Assert.Equal(0f, data[10]);         // right padding
        Assert.Equal(0f, data[319]);
    }

    [Fact]
    public void DbPostProcess_KnownRectangle_IsExpandedByUnclipDistance()
    {
        int w = 160, h = 64;
        var pred = new float[w * h];
        for (int y = 20; y < 40; y++)
            for (int x = 30; x < 130; x++)
                pred[y * w + x] = 0.9f;
        var boxes = DbPostProcessor.Process(pred, w, h, w, h, new DbParameters(0.3f, 0.6f, 1.5, 1000));
        var b = Assert.Single(boxes);
        Assert.Equal(0.9, b.Score, 4);
        // Contour hull spans pixels 30..129 × 20..39 → box 99 × 19; distance = 99·19·1.5 / (2·(99+19)) ≈ 11.95.
        double d = 99 * 19 * 1.5 / (2 * (99 + 19.0));
        Assert.InRange(b.Points[0].X, 30 - d - 1, 30 - d + 1);
        Assert.InRange(b.Points[0].Y, 20 - d - 1, 20 - d + 1);
        Assert.InRange(b.Points[2].X, 129 + d - 1, 129 + d + 1);
        Assert.InRange(b.Points[2].Y, 39 + d - 1, 39 + d + 1);
        // Order: top-left, top-right, bottom-right, bottom-left.
        Assert.True(b.Points[0].X < b.Points[1].X && b.Points[1].Y < b.Points[2].Y && b.Points[3].X < b.Points[2].X);
    }

    [Fact]
    public void DbPostProcess_ScalesToTheOriginalImageAndDropsWeakOrTinyBlobs()
    {
        int w = 128, h = 64;
        var pred = new float[w * h];
        void Fill(int x0, int y0, int x1, int y1, float v) { for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) pred[y * w + x] = v; }
        Fill(10, 10, 60, 22, 0.95f);  // strong
        Fill(70, 10, 120, 22, 0.35f); // above thresh, below box_thresh → dropped
        Fill(10, 40, 12, 42, 0.95f);  // 2×2 → short side < 3 → dropped
        var boxes = DbPostProcessor.Process(pred, w, h, w * 2, h * 2, new DbParameters(0.3f, 0.6f, 1.5, 1000));
        var b = Assert.Single(boxes);
        Assert.InRange(b.Points[0].X, 2 * 10 - 2 * 8, 2 * 10);  // scaled ×2 (and expanded)
        Assert.True(b.Points.All(p => p.X >= 0 && p.Y >= 0 && p.X <= w * 2 && p.Y <= h * 2));
    }

    [Fact]
    public void DbPostProcess_ClipsBoxesToTheImage()
    {
        int w = 64, h = 32;
        var pred = new float[w * h];
        for (int y = 0; y < 10; y++) for (int x = 0; x < 40; x++) pred[y * w + x] = 0.9f;
        var b = Assert.Single(DbPostProcessor.Process(pred, w, h, w, h, new DbParameters(0.3f, 0.6f, 1.5, 1000)));
        Assert.Equal((0, 0), b.Points[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(30)]
    [InlineData(-45)]
    [InlineData(60)]
    public void MinAreaRect_OfRotatedRectangle_RecoversItsSize(double degrees)
    {
        double a = degrees * Math.PI / 180, cx = 100, cy = 80, hw = 40, hh = 10;
        var pts = new List<(int X, int Y)>();
        for (double u = -hw; u <= hw; u += 0.5)
            for (double v = -hh; v <= hh; v += 0.5)
                pts.Add(((int)Math.Round(cx + u * Math.Cos(a) - v * Math.Sin(a)), (int)Math.Round(cy + u * Math.Sin(a) + v * Math.Cos(a))));
        var r = MinAreaRect.Compute(pts);
        var (lo, hi) = (Math.Min(r.Width, r.Height), Math.Max(r.Width, r.Height));
        Assert.InRange(hi, 2 * hw - 1.5, 2 * hw + 1.5);
        Assert.InRange(lo, 2 * hh - 1.5, 2 * hh + 1.5);
        Assert.InRange(r.CenterX, cx - 1, cx + 1);
        var (box, sside) = MinAreaRect.GetMiniBox(pts);
        Assert.Equal(lo, sside);
        Assert.True(box[0].X <= box[1].X + 1e-3 && box[3].X <= box[2].X + 1e-3, "corner order: left points first");
    }

    [Fact]
    public void Ctc_MergesRepeatsDropsBlanksAndKeepsSpaces()
    {
        var dict = new CharacterDictionary(["a", "b"]); // classes: blank, a, b, space
        Assert.Equal(4, dict.ClassCount);
        int[] seq = [1, 1, 0, 1, 2, 2, 3, 3, 2, 0];
        var probs = new float[seq.Length * 4];
        var kept = new List<float>();
        for (int t = 0; t < seq.Length; t++)
        {
            for (int c = 0; c < 4; c++) probs[t * 4 + c] = 0.05f;
            float p = 0.6f + 0.03f * t;
            probs[t * 4 + seq[t]] = p;
            bool keep = (t == 0 || seq[t] != seq[t - 1]) && seq[t] != 0;
            if (keep) kept.Add(p);
        }
        var (text, conf) = CtcDecoder.Decode(probs, seq.Length, 4, dict);
        Assert.Equal("aab b", text);
        Assert.Equal(kept.Average(), conf, 5);
    }

    [Fact]
    public void Ctc_AllBlank_IsEmptyWithZeroConfidence()
    {
        var dict = new CharacterDictionary(["a"]);
        var probs = new float[] { 0.9f, 0.05f, 0.05f, 0.8f, 0.1f, 0.1f };
        Assert.Equal(("", 0f), CtcDecoder.Decode(probs, 2, 3, dict));
    }

    [Fact]
    public void Ctc_ClassCountMismatch_Throws()
    {
        var dict = new CharacterDictionary(["a"]);
        Assert.Throws<ArgumentException>(() => CtcDecoder.Decode(new float[8], 2, 4, dict));
    }

    [Fact]
    public void CharacterDictionary_WithoutSpace_HasOneClassLess()
    {
        Assert.Equal(3, new CharacterDictionary(["a", "b"], useSpaceChar: false).ClassCount);
    }

    [Theory]
    [InlineData("1./255.", 1.0 / 255.0)]
    [InlineData("0.00392156862745098", 0.00392156862745098)]
    [InlineData("2", 2.0)]
    public void ScaleExpressions_AreParsed(string text, double expected)
    {
        Assert.Equal(expected, PaddleModelConfig.ParseExpression(text, "x.yml"), 12);
    }
}
