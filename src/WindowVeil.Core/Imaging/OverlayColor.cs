using System.Globalization;

namespace WindowVeil.Imaging;

/// <summary>An 8-bit-per-channel ARGB color that does not depend on WPF or GDI.</summary>
public readonly record struct OverlayColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Opaque black.</summary>
    public static OverlayColor Black => new(255, 0, 0, 0);

    /// <summary>Opaque white.</summary>
    public static OverlayColor White => new(255, 255, 255, 255);

    /// <summary>Fully transparent.</summary>
    public static OverlayColor Transparent => new(0, 0, 0, 0);

    /// <summary>Creates an opaque color.</summary>
    public static OverlayColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c> (the leading <c>#</c> is optional).
    /// </summary>
    /// <exception cref="FormatException">The text is not a supported color.</exception>
    public static OverlayColor Parse(string text)
    {
        if (TryParse(text, out var c)) return c;
        throw new FormatException($"'{text}' is not a valid color. Use #RGB, #RRGGBB or #AARRGGBB.");
    }

    /// <summary>Attempts to parse a hex color.</summary>
    public static bool TryParse(string? text, out OverlayColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
        switch (s.Length)
        {
            case 3:
                byte r = (byte)(((v >> 8) & 0xF) * 17), g = (byte)(((v >> 4) & 0xF) * 17), b = (byte)((v & 0xF) * 17);
                color = new OverlayColor(255, r, g, b);
                return true;
            case 6:
                color = new OverlayColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                return true;
            case 8:
                color = new OverlayColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Returns the same color with a different alpha.</summary>
    public OverlayColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>Largest per-channel difference to another color, ignoring alpha.</summary>
    public int MaxChannelDifference(OverlayColor other) =>
        Math.Max(Math.Abs(R - other.R), Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));

    /// <summary>Formats as <c>#RRGGBB</c> when opaque, otherwise <c>#AARRGGBB</c>.</summary>
    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
