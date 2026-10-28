using System.Buffers.Binary;
using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.UnitTests.Core;

public class OverlayColorTests
{
    [Theory]
    [InlineData("#F80", 255, 0xFF, 0x88, 0x00)]
    [InlineData("f80", 255, 0xFF, 0x88, 0x00)]
    [InlineData("#202020", 255, 0x20, 0x20, 0x20)]
    [InlineData("  #a1B2c3 ", 255, 0xA1, 0xB2, 0xC3)]
    [InlineData("#80FF0000", 0x80, 0xFF, 0x00, 0x00)]
    [InlineData("#00000000", 0, 0, 0, 0)]
    public void Parse_SupportedFormats(string text, int a, int r, int g, int b)
    {
        Assert.Equal(new OverlayColor((byte)a, (byte)r, (byte)g, (byte)b), OverlayColor.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#-12345")]
    [InlineData("# 123")]
    public void TryParse_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(OverlayColor.TryParse(text, out var c));
        Assert.Equal(default, c);
        if (text is not null) Assert.Throws<FormatException>(() => OverlayColor.Parse(text));
    }

    [Fact]
    public void ToString_OpaqueAsRgb_TranslucentAsArgb_RoundTrips()
    {
        Assert.Equal("#202020", OverlayColor.FromRgb(0x20, 0x20, 0x20).ToString());
        Assert.Equal("#80FF0000", new OverlayColor(0x80, 0xFF, 0, 0).ToString());
        foreach (var c in new[] { OverlayColor.Black, OverlayColor.White, OverlayColor.Transparent, new OverlayColor(1, 2, 3, 4) })
            Assert.Equal(c, OverlayColor.Parse(c.ToString()));
    }

    [Fact]
    public void WithAlpha_AndMaxChannelDifference()
    {
        var c = OverlayColor.FromRgb(10, 200, 30).WithAlpha(7);
        Assert.Equal(7, c.A);
        Assert.Equal(170, c.MaxChannelDifference(OverlayColor.FromRgb(40, 30, 30)));
    }
}

public class PngCodecTests
{
    private const int W = 5, H = 4;

    // Generated with an explicit filter per row and verified with Pillow (see the report for the generator).
    public const string Rgba_Filter0 = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAX0lEQVR4nAFUAKv/AAADAP8yCgvrZBEs15YYY8PIH7CvAA0/HfU/RijhcU1JzaNUgLnVW82lABp7OutMgkXXfolmw7CQna/il+qbACe3V+FZvmLNi8WDub3MuqXv0weR5Iso4VzkNJMAAAAASUVORK5CYII=";
    public const string Rgba_Filter1 = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAALklEQVR4nGNkYGb4b8TO/caIXRGIzYHY9w0jr73sVwxBqWqr1xiC6tvDH6ILAgA9RhtFRD17pAAAAABJRU5ErkJggg==";
    public const string Rgba_Filter2 = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAJ0lEQVR4nGNiYGb4b8TF/TpFUOf6NInkwyfkN6xn4rWR/YaOiRcEAJvIHDM2bhhsAAAAAElFTkSuQmCC";
    public const string Rgba_Filter3 = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAPElEQVR4nGNmYGb4b8TJnePNo56UIuAbUStc78fMaydbpqAk8lFBSR6ItYDY9COzSIxOIYagVJVVDrogAKvZGFab9xuIAAAAAElFTkSuQmCC";
    public const string Rgba_Filter4 = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAKElEQVR4nGNhYGb4b8TO/caIXRGIzYHY9w0Lr43sN152biCWhWPiBQHjYRgJxvmxCgAAAABJRU5ErkJggg==";
    public const string Rgba_MixedFilters = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAYAAABGM/VAAAAAPElEQVR4nGNkYGb4b8TO/caIXRGIzYHY9w0Tr43sN3TMLBKjU6igJPJRQUkeiLWA2PQjC1iWnRuIZeEYAB1OGW5zNzVkAAAAAElFTkSuQmCC";
    public const string Rgb_Paeth = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAIAAADJUWIXAAAAOUlEQVR4nGNhYGYwYuc2Ylc0Yjc3Yvdl5rWTVVASUVCSV1DSUlAyZeG1keVl5+Zll4UgZqkqK2R5ANd8Byi7jM0DAAAAAElFTkSuQmCC";
    public const string Gray_Sub = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAAAAABjWKqcAAAAE0lEQVR4nGNkMAICJl4QYJRCsAEcxAIzHxHRHgAAAABJRU5ErkJggg==";
    public const string GrayAlpha_Avg = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAQAAADsOj3LAAAAIElEQVR4nGNm+G+U452UElHrx8L7DQaZRQoVPkIgkigAgf8Sx1P6yDAAAAAASUVORK5CYII=";
    public const string Palette_Up = "iVBORw0KGgoAAAANSUhEUgAAAAUAAAAECAMAAABx7QVyAAAADFBMVEUKFB7IZDIA/wD///9aDoMRAAAAA3RSTlP/gAB/bWh4AAAAGklEQVR4nGNiYGRiZgATjEyMjH8ZGRlALGYACmkBHDHkAdkAAAAASUVORK5CYII=";

    private static (byte R, byte G, byte B, byte A) Px(int x, int y) => (
        (byte)((x * 50 + y * 13) & 255),
        (byte)((x * 7 + y * 60 + 3) & 255),
        (byte)((x * x * 11 + y * 29) & 255),
        (byte)((255 - (x * 20 + y * 10)) & 255));

    public static TheoryData<string, string> Fixtures => new()
    {
        { nameof(Rgba_Filter0), Rgba_Filter0 },
        { nameof(Rgba_Filter1), Rgba_Filter1 },
        { nameof(Rgba_Filter2), Rgba_Filter2 },
        { nameof(Rgba_Filter3), Rgba_Filter3 },
        { nameof(Rgba_Filter4), Rgba_Filter4 },
        { nameof(Rgba_MixedFilters), Rgba_MixedFilters },
        { nameof(Rgb_Paeth), Rgb_Paeth },
        { nameof(Gray_Sub), Gray_Sub },
        { nameof(GrayAlpha_Avg), GrayAlpha_Avg },
        { nameof(Palette_Up), Palette_Up },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_FilteredFixtures_ProducesExpectedPixels(string name, string base64)
    {
        var image = PngCodec.Decode(Convert.FromBase64String(base64));

        Assert.Equal(new PixelSize(W, H), image.Size);
        var palette = new[] { OverlayColor.FromRgb(10, 20, 30), new OverlayColor(128, 200, 100, 50), new OverlayColor(0, 0, 255, 0), OverlayColor.White };
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var (r, g, b, a) = Px(x, y);
                var expected = name switch
                {
                    _ when name.StartsWith("Rgba", StringComparison.Ordinal) => new OverlayColor(a, r, g, b),
                    nameof(Rgb_Paeth) => new OverlayColor(255, r, g, b),
                    nameof(Gray_Sub) => new OverlayColor(255, r, r, r),
                    nameof(GrayAlpha_Avg) => new OverlayColor(a, r, r, r),
                    _ => palette[(x + y) % 4],
                };
                Assert.True(expected == image.GetPixel(x, y), $"{name} pixel ({x},{y}): expected {expected}, got {image.GetPixel(x, y)}");
            }
        }
    }

    [Fact]
    public void EncodeDecode_RoundTripsEveryPixel()
    {
        var image = new BgraImage(7, 3);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 7; x++)
                image.SetPixel(x, y, new OverlayColor((byte)(x * 30), (byte)(y * 80), (byte)(x * y * 9), (byte)(255 - x)));

        var decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(image.Size, decoded.Size);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Decode_PaddedStride_IsHandledOnEncode()
    {
        var pixels = new byte[3 * 2 * 4 + 2 * 8];
        var image = new BgraImage(3, 2, pixels, stride: 3 * 4 + 8);
        image.SetPixel(2, 1, OverlayColor.FromRgb(1, 2, 3));

        var decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(OverlayColor.FromRgb(1, 2, 3), decoded.GetPixel(2, 1));
        Assert.Equal(new OverlayColor(0, 0, 0, 0), decoded.GetPixel(0, 0));
    }

    [Fact]
    public void SaveLoad_File_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wv-{Guid.NewGuid():n}.png");
        try
        {
            var image = new BgraImage(2, 2);
            image.FillRect(new PixelRect(0, 0, 2, 2), OverlayColor.FromRgb(9, 8, 7));
            PngCodec.Save(image, path);
            Assert.Equal(image.Pixels, PngCodec.Load(path).Pixels);
        }
        finally { File.Delete(path); }
    }

    public static TheoryData<string, byte[]> InvalidPngs()
    {
        var valid = Convert.FromBase64String(Rgba_Filter1);
        var badSignature = (byte[])valid.Clone();
        badSignature[1] = (byte)'X';
        var truncated = valid[..40];
        var badFilter = Rebuild(raw => raw[0] = 9);
        var sixteenBit = PatchIhdr(valid, ihdr => ihdr[8] = 16);
        var interlaced = PatchIhdr(valid, ihdr => ihdr[12] = 1);
        var badColorType = PatchIhdr(valid, ihdr => ihdr[9] = 5);
        var paletteWithoutPlte = PatchIhdr(valid, ihdr => ihdr[9] = 3);
        var shortIhdr = Png([Chunk("IHDR", new byte[4]), Chunk("IEND", [])]);
        var huge = PatchIhdr(valid, ihdr => { BinaryPrimitives.WriteInt32BigEndian(ihdr, 1 << 20); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1 << 20); });
        var wrapping = PatchIhdr(valid, ihdr => { BinaryPrimitives.WriteInt32BigEndian(ihdr, 1 << 30); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 4); });
        var chunkOverflow = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32BigEndian(chunkOverflow.AsSpan(33), int.MaxValue - 4); // IDAT length
        var noIhdr = Png([Chunk("IEND", [])]);
        var truncatedData = Rebuild(null, dropBytes: 10);
        return new()
        {
            { "empty", [] },
            { "bad signature", badSignature },
            { "truncated chunk", truncated },
            { "unknown filter", badFilter },
            { "16-bit", sixteenBit },
            { "interlaced", interlaced },
            { "bad color type", badColorType },
            { "palette without PLTE", paletteWithoutPlte },
            { "short IHDR", shortIhdr },
            { "huge dimensions", huge },
            { "dimensions that wrap int arithmetic", wrapping },
            { "chunk length overflow", chunkOverflow },
            { "no IHDR", noIhdr },
            { "truncated image data", truncatedData },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidPngs))]
    public void Decode_InvalidData_ThrowsInvalidDataException(string reason, byte[] data)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PngCodec.Decode(data));
        Assert.False(string.IsNullOrEmpty(ex.Message), reason);
    }

    // ---- helpers to build malformed PNGs

    private static byte[] PatchIhdr(byte[] png, Action<byte[]> patch)
    {
        var copy = (byte[])png.Clone();
        var ihdr = copy.AsSpan(16, 13).ToArray(); // signature(8) + length(4) + type(4)
        patch(ihdr);
        ihdr.CopyTo(copy, 16);
        return copy; // CRCs are not verified by the decoder
    }

    private static byte[] Rebuild(Action<byte[]>? patchRaw, int dropBytes = 0)
    {
        // A 1x1 RGBA image whose raw scanline data is patched before compression.
        var raw = new byte[] { 0, 1, 2, 3, 4 };
        patchRaw?.Invoke(raw);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var idat = ms.ToArray();
        if (dropBytes > 0)
        {
            // Valid zlib stream that decompresses to fewer bytes than the image needs.
            using var ms2 = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(ms2, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw.AsSpan(0, 2));
            idat = ms2.ToArray();
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;
        ihdr[9] = 6;
        return Png([Chunk("IHDR", ihdr), Chunk("IDAT", idat), Chunk("IEND", [])]);
    }

    private static byte[] Png(byte[][] chunks) =>
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Concat(chunks.SelectMany(c => c)).ToArray();

    private static byte[] Chunk(string type, byte[] body)
    {
        var c = new byte[12 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(c, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(c, 4);
        body.CopyTo(c, 8);
        return c; // CRC left as zero
    }
}

public class BgraImageTests
{
    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraImage(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraImage(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BgraImage(2, 1, new byte[8], stride: 4));
        Assert.Throws<ArgumentException>(() => new BgraImage(2, 2, new byte[8]));
    }

    [Fact]
    public void FillCropBlit_ClipToBounds()
    {
        var image = new BgraImage(4, 4);
        image.FillRect(new PixelRect(-2, -2, 4, 4), OverlayColor.White);
        Assert.Equal(OverlayColor.White, image.GetPixel(1, 1));
        Assert.Equal(OverlayColor.Transparent, image.GetPixel(2, 2));

        var crop = image.Crop(new PixelRect(1, 1, 10, 10));
        Assert.Equal(new PixelSize(3, 3), crop.Size);
        Assert.Equal(OverlayColor.White, crop.GetPixel(0, 0));
        Assert.Throws<ArgumentException>(() => image.Crop(new PixelRect(10, 10, 2, 2)));

        var target = new BgraImage(3, 3);
        target.Blit(crop, 1, 1);
        Assert.Equal(OverlayColor.White, target.GetPixel(1, 1));
        Assert.Equal(OverlayColor.Transparent, target.GetPixel(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => target.GetPixel(3, 0));

        var clone = target.Clone();
        clone.SetPixel(0, 0, OverlayColor.Black);
        Assert.Equal(OverlayColor.Transparent, target.GetPixel(0, 0));
    }
}
