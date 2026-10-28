using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.RetroCapDemo.Evidence;

/// <summary>
/// Builds the before/after comparison image from two real screenshots: crops the same area around the target window
/// from both, places them side by side with labels, and outlines the target control in each.
/// </summary>
public static class ComparisonImage
{
    /// <summary>Renders the comparison and saves it as PNG. Runs on a private STA thread (WPF imaging).</summary>
    public static void Create(BgraImage before, BgraImage after, PixelRect screenOrigin, PixelRect cropScreenRect,
        PixelRect highlightScreenRect, string beforeLabel, string afterLabel, string title, string outputPath)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Render(before, after, screenOrigin, cropScreenRect, highlightScreenRect, beforeLabel, afterLabel, title, outputPath); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Failed to render the comparison image.", error);
    }

    private static void Render(BgraImage before, BgraImage after, PixelRect origin, PixelRect crop, PixelRect highlight,
        string beforeLabel, string afterLabel, string title, string outputPath)
    {
        var cropImg = ImageAnalysis.ToImage(before, origin, crop);
        if (cropImg.IsEmpty) throw new ArgumentException("Crop area is outside the screenshots.");
        var left = ToBitmap(before.Crop(cropImg));
        var right = ToBitmap(after.Crop(cropImg));
        var hl = highlight.Offset(-origin.X - cropImg.X, -origin.Y - cropImg.Y);

        const int margin = 24, header = 64, label = 36, gap = 24;
        int w = margin * 2 + cropImg.Width * 2 + gap;
        int h = header + label + cropImg.Height + margin + 28;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)), null, new Rect(0, 0, w, h));
            Text(dc, title, typeface, 22, Brushes.Black, new Point(margin, 18));
            for (int i = 0; i < 2; i++)
            {
                double x = margin + i * (cropImg.Width + gap);
                Text(dc, i == 0 ? beforeLabel : afterLabel, typeface, 16, i == 0 ? Brushes.DimGray : Brushes.DarkGreen, new Point(x, header));
                dc.DrawImage(i == 0 ? left : right, new Rect(x, header + label, cropImg.Width, cropImg.Height));
                dc.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(x - 0.5, header + label - 0.5, cropImg.Width + 1, cropImg.Height + 1));
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), 2) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(null, pen, new Rect(x + hl.X - 4, header + label + hl.Y - 4, hl.Width + 8, hl.Height + 8));
            }
            Text(dc, "Real screenshots of the unmodified RetroCap v1.0 window. Red outline: the StartupCheck control. Other controls are unchanged.",
                new Typeface("Segoe UI"), 12, Brushes.DimGray, new Point(margin, h - 26));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static void Text(DrawingContext dc, string text, Typeface typeface, double size, Brush brush, Point at) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, 1.0), at);

    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bmp.Freeze();
        return bmp;
    }
}
