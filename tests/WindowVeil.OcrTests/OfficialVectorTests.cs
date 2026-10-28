using System.Text.Json;
using WindowVeil.Ocr.Paddle;
using Xunit;

namespace WindowVeil.OcrTests;

/// <summary>
/// Compares the C# re-implementations with outputs of the official PaddleX processors / OpenCV / pyclipper recorded in
/// Fixtures/vectors.json (see Fixtures/generate_vectors.py). These tests need no models.
/// </summary>
public class OfficialVectorTests
{
    private static JsonElement V(string name) => TestEnvironment.Vectors.GetProperty(name);

    private static byte[] B64(JsonElement e) => Convert.FromBase64String(e.GetString()!);

    private static float[] F32(JsonElement e)
    {
        var bytes = B64(e);
        var f = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
        return f;
    }

    private static (int X, int Y)[] IntPoints(JsonElement e) => e.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToArray();

    private static (float X, float Y)[] FloatPoints(JsonElement e) => e.EnumerateArray().Select(p => ((float)p[0].GetDouble(), (float)p[1].GetDouble())).ToArray();

    [Fact]
    public void ResizeLinear_IsBitExactWithOpenCv()
    {
        foreach (var c in V("resize").EnumerateArray())
        {
            int h = c.GetProperty("h").GetInt32(), w = c.GetProperty("w").GetInt32();
            int dh = c.GetProperty("dh").GetInt32(), dw = c.GetProperty("dw").GetInt32();
            var src = new BgrImage(w, h, B64(c.GetProperty("src")));
            var expected = B64(c.GetProperty("dst"));
            var actual = ImageOps.ResizeLinear(src, dw, dh);
            int diffs = expected.Where((v, i) => v != actual.Data[i]).Count();
            Assert.True(diffs == 0, $"{w}x{h} -> {dw}x{dh}: {diffs} of {expected.Length} bytes differ from cv2.resize");
        }
    }

    [Fact]
    public void MinAreaRect_MatchesOpenCvAndGetMiniBoxes()
    {
        int n = 0;
        foreach (var c in V("minAreaRect").EnumerateArray())
        {
            var pts = IntPoints(c.GetProperty("points"));
            var (box, sside) = MinAreaRect.GetMiniBox(pts);
            var expected = FloatPoints(c.GetProperty("box"));
            for (int i = 0; i < 4; i++)
            {
                Assert.True(Math.Abs(box[i].X - expected[i].X) < 1e-3 && Math.Abs(box[i].Y - expected[i].Y) < 1e-3,
                    $"case {n} corner {i}: got ({box[i].X}, {box[i].Y}) expected ({expected[i].X}, {expected[i].Y})");
            }
            Assert.Equal(c.GetProperty("sside").GetDouble(), sside, 3);
            n++;
        }
        Assert.True(n >= 40);
    }

    [Fact]
    public void Unclip_MatchesPyclipperOffset()
    {
        int n = 0;
        foreach (var c in V("unclip").EnumerateArray())
        {
            var box = FloatPoints(c.GetProperty("box"));
            var expanded = DbPostProcessor.Unclip(box, c.GetProperty("ratio").GetDouble());
            var expectedSet = IntPoints(c.GetProperty("expanded")).Select(p => ((long)p.X, (long)p.Y)).ToHashSet();
            // Clipper's final union may drop collinear vertices; every vertex it keeps must be one we produced, and the
            // min-area rectangle of both must agree.
            Assert.True(expectedSet.IsSubsetOf(expanded.ToHashSet()), $"case {n}: offset vertices differ from pyclipper");
            var (mini, sside) = MinAreaRect.GetMiniBox(expanded.Select(p => ((int)p.X, (int)p.Y)).ToArray());
            var expectedMini = FloatPoints(c.GetProperty("mini"));
            for (int i = 0; i < 4; i++)
                Assert.True(Math.Abs(mini[i].X - expectedMini[i].X) < 1e-3 && Math.Abs(mini[i].Y - expectedMini[i].Y) < 1e-3, $"case {n} mini corner {i}");
            Assert.Equal(c.GetProperty("sside").GetDouble(), sside, 3);
            n++;
        }
    }

    [Fact]
    public void DbPostProcess_MatchesOfficialOnSyntheticMaps()
    {
        int n = 0, totalBoxes = 0;
        foreach (var c in V("db").EnumerateArray())
        {
            int h = c.GetProperty("h").GetInt32(), w = c.GetProperty("w").GetInt32();
            var q = B64(c.GetProperty("pred_q1000"));
            var pred = new float[w * h];
            for (int i = 0; i < pred.Length; i++) pred[i] = (float)(BitConverter.ToUInt16(q, i * 2) / 1000.0);
            var p = new DbParameters((float)c.GetProperty("thresh").GetDouble(), (float)c.GetProperty("box_thresh").GetDouble(),
                c.GetProperty("unclip_ratio").GetDouble(), 3000);
            var boxes = DbPostProcessor.Process(pred, w, h, c.GetProperty("src_w").GetInt32(), c.GetProperty("src_h").GetInt32(), p);
            var expected = c.GetProperty("boxes").EnumerateArray().Select(IntPoints).ToArray();
            var scores = c.GetProperty("scores").EnumerateArray().Select(s => s.GetDouble()).ToArray();
            Assert.True(expected.Length == boxes.Count, $"case {n}: {boxes.Count} boxes, official {expected.Length}");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], boxes[i].Points);
                Assert.Equal(scores[i], boxes[i].Score, 4);
            }
            totalBoxes += expected.Length;
            n++;
        }
        Assert.True(totalBoxes > 5, "the synthetic maps should produce boxes");
    }

    [Fact]
    public void MinAreaRectCrop_IsBitExactWithOfficialCropByPolys()
    {
        var c = V("crop");
        var src = new BgrImage(c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32(), B64(c.GetProperty("src")));
        int n = 0;
        foreach (var k in c.GetProperty("cases").EnumerateArray())
        {
            var crop = TextLineCropper.Crop(src, IntPoints(k.GetProperty("poly")));
            Assert.NotNull(crop);
            Assert.Equal((k.GetProperty("w").GetInt32(), k.GetProperty("h").GetInt32()), (crop!.Width, crop.Height));
            var expected = B64(k.GetProperty("dst"));
            int diffs = expected.Where((v, i) => v != crop.Data[i]).Count();
            int maxDiff = expected.Select((v, i) => Math.Abs(v - crop.Data[i])).Max();
            Assert.True(diffs == 0, $"crop case {n}: {diffs} of {expected.Length} bytes differ (max {maxDiff})");
            n++;
        }
    }

    [Fact]
    public void FindContours_MatchesOpenCvRetrListChainApproxSimple()
    {
        int n = 0;
        foreach (var c in V("contours").EnumerateArray())
        {
            int h = c.GetProperty("h").GetInt32(), w = c.GetProperty("w").GetInt32();
            var bits = B64(c.GetProperty("bits")).Select(b => b != 0).ToArray();
            var actual = DbPostProcessor.FindContours(bits, w, h);
            var expected = c.GetProperty("contours").EnumerateArray().Select(IntPoints).ToArray();
            Assert.True(expected.Length == actual.Count, $"case {n}: {actual.Count} contours, OpenCV {expected.Length}");
            for (int i = 0; i < expected.Length; i++)
                Assert.True(expected[i].SequenceEqual(actual[i]), $"case {n} contour {i}: [{string.Join(" ", actual[i])}] vs OpenCV [{string.Join(" ", expected[i])}]");
            n++;
        }
    }

    [Fact]
    public void SortQuadBoxes_MatchesOfficial()
    {
        foreach (var c in V("sort").EnumerateArray())
        {
            var boxes = c.GetProperty("boxes").EnumerateArray().Select(b => new DetectedBox(IntPoints(b), 1)).ToArray();
            var sorted = BoxSorter.Sort(boxes).Select(b => b.Points).ToArray();
            var expected = c.GetProperty("sorted").EnumerateArray().Select(IntPoints).ToArray();
            Assert.Equal(expected, sorted);
        }
    }

    [Fact]
    public void DetResizeForTest_ShapesMatchOfficial()
    {
        foreach (var c in V("detResize").EnumerateArray())
        {
            var type = c.GetProperty("limit_type").GetString() switch { "min" => DetectionLimitType.Min, "max" => DetectionLimitType.Max, _ => DetectionLimitType.ResizeLong };
            var r = DetectionPreprocessor.ComputeResize(c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32(),
                c.GetProperty("limit_side_len").GetInt32(), type, c.GetProperty("max_side_limit").GetInt32());
            Assert.Equal((c.GetProperty("rw").GetInt32(), c.GetProperty("rh").GetInt32()), (r.Width, r.Height));
            Assert.Equal(0, r.Width % 32);
            Assert.Equal(0, r.Height % 32);
        }
    }

    [Fact]
    public void RecognitionBatch_MatchesOfficialResizeNormAndPadding()
    {
        foreach (var c in V("recResize").EnumerateArray())
        {
            var crops = c.GetProperty("images").EnumerateArray()
                .Select(i => new BgrImage(i.GetProperty("w").GetInt32(), i.GetProperty("h").GetInt32(), B64(i.GetProperty("src")))).ToArray();
            var (data, width) = RecognitionPreprocessor.PrepareBatch(crops, 48, 320);
            var shape = c.GetProperty("shape").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(shape, new[] { crops.Length, 3, 48, width });
            double sum = data.Sum(v => (double)v), absSum = data.Sum(v => Math.Abs((double)v));
            Assert.Equal(c.GetProperty("sum").GetDouble(), sum, 2);
            Assert.Equal(c.GetProperty("abs_sum").GetDouble(), absSum, 2);
            if (c.GetProperty("data").ValueKind == JsonValueKind.String)
                Assert.Equal(F32(c.GetProperty("data")), data);
        }
    }

    [Fact]
    public void CtcDecode_MatchesOfficialCtcLabelDecode()
    {
        var v = V("ctc");
        var dict = new CharacterDictionary(v.GetProperty("characters").EnumerateArray().Select(x => x.GetString()!).ToArray());
        foreach (var c in v.GetProperty("cases").EnumerateArray())
        {
            var probs = F32(c.GetProperty("probs"));
            var (text, score) = CtcDecoder.Decode(probs, c.GetProperty("t").GetInt32(), c.GetProperty("c").GetInt32(), dict);
            Assert.Equal(c.GetProperty("text").GetString(), text);
            Assert.Equal((float)c.GetProperty("score").GetDouble(), score);
        }
    }
}
