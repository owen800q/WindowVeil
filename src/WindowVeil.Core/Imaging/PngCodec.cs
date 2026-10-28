using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace WindowVeil.Imaging;

/// <summary>
/// A small, dependency-free PNG encoder/decoder for <see cref="BgraImage"/>. It decodes non-interlaced
/// 8-bit grayscale, gray+alpha, RGB, RGBA and palette images, which covers screenshots and test fixtures.
/// It is not a general-purpose image library.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Encodes an image as a 32-bit RGBA PNG.</summary>
    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    row[0] = 0; // filter: none
                    int src = y * image.Stride;
                    for (int x = 0; x < image.Width; x++)
                    {
                        int s = src + x * 4, d = 1 + x * 4;
                        row[d] = image.Pixels[s + 2];
                        row[d + 1] = image.Pixels[s + 1];
                        row[d + 2] = image.Pixels[s];
                        row[d + 3] = image.Pixels[s + 3];
                    }
                    z.Write(row);
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>Encodes and writes an image to a file.</summary>
    public static void Save(BgraImage image, string path) => File.WriteAllBytes(path, Encode(image));

    /// <summary>Reads and decodes a PNG file.</summary>
    public static BgraImage Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Decodes a PNG.</summary>
    /// <exception cref="InvalidDataException">The data is not a supported PNG.</exception>
    public static BgraImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG file (bad signature).");
        int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
            if (len < 0 || pos + 12 + len > data.Length) throw new InvalidDataException("Truncated PNG chunk.");
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
            var body = data.Slice(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    bitDepth = body[8]; colorType = body[9]; interlace = body[12];
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": paletteAlpha = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            pos += 12 + len;
            if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid IHDR chunk.");
        if (bitDepth != 8) throw new InvalidDataException($"Unsupported PNG bit depth {bitDepth}; only 8-bit images are supported.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNGs are not supported.");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}.") };
        if (colorType == 3 && palette is null) throw new InvalidDataException("Palette PNG without PLTE chunk.");

        int rowBytes = width * channels;
        var raw = new byte[(rowBytes + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n == 0) throw new InvalidDataException("PNG image data is truncated.");
                read += n;
            }
        }

        var image = new BgraImage(width, height);
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (rowBytes + 1)];
            Array.Copy(raw, y * (rowBytes + 1) + 1, cur, 0, rowBytes);
            Unfilter(filter, cur, prev, channels);
            int dst = y * image.Stride;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                int s = x * channels;
                switch (colorType)
                {
                    case 0: r = g = b = cur[s]; break;
                    case 4: r = g = b = cur[s]; a = cur[s + 1]; break;
                    case 2: r = cur[s]; g = cur[s + 1]; b = cur[s + 2]; break;
                    case 6: r = cur[s]; g = cur[s + 1]; b = cur[s + 2]; a = cur[s + 3]; break;
                    default:
                        int idx = cur[s];
                        if (idx * 3 + 2 >= palette!.Length) throw new InvalidDataException("Palette index out of range.");
                        r = palette[idx * 3]; g = palette[idx * 3 + 1]; b = palette[idx * 3 + 2];
                        if (paletteAlpha is not null && idx < paletteAlpha.Length) a = paletteAlpha[idx];
                        break;
                }
                int d = dst + x * 4;
                image.Pixels[d] = b; image.Pixels[d + 1] = g; image.Pixels[d + 2] = r; image.Pixels[d + 3] = a;
            }
            (prev, cur) = (cur, prev);
        }
        return image;
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: return;
            case 1: for (int i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp]; return;
            case 2: for (int i = 0; i < cur.Length; i++) cur[i] += prev[i]; return;
            case 3:
                for (int i = 0; i < cur.Length; i++)
                    cur[i] += (byte)(((i >= bpp ? cur[i - bpp] : 0) + prev[i]) / 2);
                return;
            case 4:
                for (int i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return;
            default: throw new InvalidDataException($"Unknown PNG filter type {filter}.");
        }
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        s.Write(header);
        s.Write(body);
        var crc = new Crc32();
        crc.Append(header[4..]);
        crc.Append(body);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        s.Write(crcBytes);
    }
}
