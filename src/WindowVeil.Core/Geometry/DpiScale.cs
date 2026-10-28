namespace WindowVeil.Geometry;

/// <summary>
/// Monitor DPI expressed as a scale relative to 96 DPI (100%). WPF device-independent pixels
/// (DIPs) are 1/96 inch; one DIP equals <see cref="Scale"/> physical pixels.
/// </summary>
public readonly record struct DpiScale
{
    /// <summary>Creates a scale from a raw DPI value (96 = 100%).</summary>
    public DpiScale(double dpi)
    {
        if (double.IsNaN(dpi) || dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPI must be positive.");
        Dpi = dpi;
    }

    /// <summary>The 96 DPI (100%) scale.</summary>
    public static DpiScale Default => new(96);

    /// <summary>Creates a scale from a percentage, e.g. 150 for 150% scaling.</summary>
    public static DpiScale FromPercent(double percent) => new(96 * percent / 100.0);

    /// <summary>Raw DPI value.</summary>
    public double Dpi { get; }

    /// <summary>Physical pixels per DIP (1.0 at 96 DPI, 1.5 at 144 DPI).</summary>
    public double Scale => (Dpi == 0 ? 96 : Dpi) / 96.0;

    /// <summary>Scaling percentage (100, 125, 150, ...).</summary>
    public double Percent => Scale * 100;

    /// <inheritdoc />
    public override string ToString() => $"{Dpi:0} DPI ({Percent:0}%)";
}
