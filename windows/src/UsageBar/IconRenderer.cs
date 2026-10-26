using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Draws the notification-area icons at their real pixel size. Monochrome: white on a dark taskbar, black on a light
/// one, never coloured by usage (decision 4). Shapes sit on whole pixels so they stay crisp at 16, 20 and 24 px.
/// </summary>
internal static class IconRenderer
{
    static Geometry? spark;

    /// <summary>The Claude spark from design/spark/ClaudeSpark.svg (a 100 x 100 viewBox).</summary>
    public static Geometry Spark => spark ??= LoadSpark();

    static Geometry LoadSpark()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeSpark.svg")
                      ?? throw new InvalidOperationException("spark resource missing");
        var svg = new StreamReader(s).ReadToEnd();
        var d = Regex.Match(svg, "\\sd=\"([^\"]+)\"").Groups[1].Value;
        var g = Geometry.Parse(d);
        g.Freeze();
        return g;
    }

    public static BitmapSource Render(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var dc = visual.RenderOpen()) draw(dc);
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    static Brush Solid(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Bar thickness and gap for a stack of <paramref name="count"/> bars filling an n-pixel icon.</summary>
    static (int H, int Gap) BarMetrics(int n, int count) => count >= 3
        ? (Math.Max(3, (int)Math.Round(n * 0.18)), Math.Max(1, (int)Math.Round(n * 0.1)))
        : (Math.Max(4, (int)Math.Round(n * 0.25)), Math.Max(2, (int)Math.Round(n * 0.14)));

    /// <summary>
    /// Stacked bars in the account's colour (decision 16): the track is the same colour faded, the fill full strength,
    /// at least a dot above 0. The colour says which account; usage never changes it.
    /// </summary>
    public static void DrawBars(DrawingContext dc, int n, IReadOnlyList<int> values, Color colour, bool dark)
    {
        var vals = values.Count > 0 ? values : new[] { 0, 0 };
        var (h, gap) = BarMetrics(n, vals.Count);
        var y = (n - (vals.Count * h + (vals.Count - 1) * gap)) / 2;
        var track = Solid(colour, dark ? 0.38 : 0.32);
        var fill = Solid(colour, 1);
        var w = n - 2;
        foreach (var v in vals)
        {
            var r = h / 2.0;
            dc.DrawRoundedRectangle(track, null, new Rect(1, y, w, h), r, r);
            var frac = Math.Clamp(v / 100.0, 0, 1);
            if (frac > 0) dc.DrawRoundedRectangle(fill, null, new Rect(1, y, Math.Max(h, Math.Round(w * frac)), h), r, r);
            y += h + gap;
        }
    }

    /// <summary>
    /// Percentages in the account's colour: one value fills the square; two (session over weekly) stack as large
    /// as they fit. A dash when there's no data.
    /// </summary>
    public static void DrawNumbers(DrawingContext dc, int n, IReadOnlyList<int> values, Color colour)
    {
        var brush = Solid(colour, 1);
        var texts = values.Count == 0 ? new[] { "–" } : values.Take(2).Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (texts.Length == 1)
        {
            var g = Glyphs(texts[0], n * 0.62, n);
            g.Transform = new TranslateTransform(Math.Round((n - g.Bounds.Width) / 2), Math.Round((n - g.Bounds.Height) / 2));
            dc.DrawGeometry(brush, null, g);
            return;
        }
        var gap = Math.Max(2, n / 10);
        var line = (n - gap) / 2.0;
        for (var i = 0; i < 2; i++)
        {
            var g = Glyphs(texts[i], Math.Floor(line), n);
            g.Transform = new TranslateTransform(Math.Round((n - g.Bounds.Width) / 2), Math.Round(i * (line + gap) + (line - g.Bounds.Height) / 2));
            dc.DrawGeometry(brush, null, g);
        }
    }

    /// <summary>The spark, inset slightly so its tips don't touch the icon edge.</summary>
    public static void DrawSpark(DrawingContext dc, int n, Color fg)
    {
        var inset = Math.Max(1, n / 16.0);
        var scale = (n - 2 * inset) / 100.0;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, inset, inset));
        dc.DrawGeometry(Solid(fg, 1), null, Spark);
        dc.Pop();
    }

    static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>
    /// Text as a geometry whose ink is <paramref name="capHeight"/> pixels tall (narrowed to <paramref name="maxWidth"/>),
    /// with its ink box moved to (0, 0) so callers can centre it exactly.
    /// </summary>
    static Geometry Glyphs(string s, double capHeight, double maxWidth)
    {
        Geometry Make(double em) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, em,
                                                      Brushes.Black, 1.0).BuildGeometry(new Point(0, 0));
        var g = Make(100);
        var b = g.Bounds;
        if (b.IsEmpty) return Geometry.Empty;
        var scale = Math.Min(capHeight / b.Height, maxWidth / b.Width);
        g = Make(100 * scale);
        b = g.Bounds;
        var group = new GeometryGroup { Children = { g } };
        group.Transform = new TranslateTransform(-b.X, -b.Y);
        return new GeometryGroup { Children = { Geometry.Combine(group, Geometry.Empty, GeometryCombineMode.Union, null) } };
    }

    // MARK: HICON

    /// <summary>Converts a rendered bitmap to an HICON. The caller destroys it with <see cref="Native.DestroyIcon"/>.</summary>
    public static IntPtr ToHIcon(BitmapSource bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var pixels = new byte[w * h * 4];
        bmp.CopyPixels(pixels, w * 4, 0);
        // Pbgra32 is premultiplied; icons want straight alpha, or edges come out dark.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var color = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = Native.CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }

    // MARK: App icon

    /// <summary>The app icon from the design/icon layers: slate gradient square, orange spark, orange and blue bars.</summary>
    public static BitmapSource AppIcon(int size) => Render(size, dc =>
    {
        var radius = size * 0.225;
        var bg = new LinearGradientBrush(Color.FromRgb(0x37, 0x38, 0x3C), Color.FromRgb(0x16, 0x16, 0x18), 90);
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), radius, radius);
        foreach (var layer in new[] { "spark.png", "bars.png" })
        {
            var img = new BitmapImage(new Uri($"pack://application:,,,/Assets/{layer}"));
            dc.DrawImage(img, new Rect(0, 0, size, size));
        }
    });

    /// <summary>Writes a multi-size .ico (PNG-compressed entries) for the build.</summary>
    public static void WriteIco(string path, int[] sizes)
    {
        var pngs = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(AppIcon(s)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var f = File.Create(path);
        using var bw = new BinaryWriter(f);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) bw.Write(p);
    }
}
