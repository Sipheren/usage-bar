using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Renders every colour preset as it appears: tray bars and numbers on a dark and a light taskbar (real size and
/// 3×), the tag badge, and a flyout bar on dark and light. For checking the presets by eye (<c>--colour-sheet</c>).
/// </summary>
internal static class ColourSheet
{
    const int N = 20;   // the tray icon size at 125% scaling
    static readonly int[] Sample = { 20, 60 };

    public static void Write(string path)
    {
        const int rowH = 72, top = 34, width = 900;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, width, top + rowH * AccountColours.All.Count + 10));
            Label(dc, "Preset", 16, 10, 12, Colors.Gray);
            Label(dc, "Dark taskbar: bars, numbers (1×, 3×)", 112, 10, 12, Colors.Gray);
            Label(dc, "Light taskbar", 354, 10, 12, Colors.Gray);
            Label(dc, "Badge", 596, 10, 12, Colors.Gray);
            Label(dc, "Flyout bar, dark / light", 650, 10, 12, Colors.Gray);
            var y = top;
            foreach (var p in AccountColours.All)
            {
                Label(dc, p.Name, 16, y + 26, 14, Colors.White);
                Tile(dc, 112, y, Color.FromRgb(0x1C, 0x1C, 0x1C), p.OnDark, true);
                Tile(dc, 354, y, Color.FromRgb(0xEE, 0xEE, 0xEE), p.OnLight, false);
                var badge = Parse(p.Badge);
                dc.DrawRoundedRectangle(new SolidColorBrush(badge), null, new Rect(600, y + 24, 20, 20), 5, 5);
                var t = Text("P", 11, Colors.White, FontWeights.Bold);
                dc.DrawText(t, new Point(610 - t.Width / 2, y + 34 - t.Height / 2));
                FlyoutBar(dc, 650, y + 14, Color.FromRgb(0x2C, 0x2C, 0x2C), Parse(p.OnDark), Colors.White);
                FlyoutBar(dc, 770, y + 14, Color.FromRgb(0xF3, 0xF3, 0xF3), Parse(p.OnLight), Colors.Black);
                y += rowH;
            }
        }
        var bmp = new RenderTargetBitmap(width, top + rowH * AccountColours.All.Count + 10, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    static void Tile(DrawingContext dc, double x, double y, Color bg, string hex, bool dark)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(x, y + 4, 230, 64), 6, 6);
        var colour = Parse(hex);
        var bars = IconRenderer.Render(N, d => IconRenderer.DrawBars(d, N, Sample, colour, dark));
        var nums = IconRenderer.Render(N, d => IconRenderer.DrawNumbers(d, N, Sample, colour));
        dc.DrawImage(bars, new Rect(x + 10, y + 26, N, N));
        dc.DrawImage(nums, new Rect(x + 38, y + 26, N, N));
        dc.DrawImage(bars, new Rect(x + 74, y + 6, N * 3, N * 3));
        dc.DrawImage(nums, new Rect(x + 150, y + 6, N * 3, N * 3));
    }

    static void FlyoutBar(DrawingContext dc, double x, double y, Color bg, Color fill, Color ink)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(x, y, 110, 40), 6, 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(36, ink.R, ink.G, ink.B)), null, new Rect(x + 8, y + 17, 94, 6), 3, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, new Rect(x + 8, y + 17, 56, 6), 3, 3);
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static FormattedText Text(string s, double size, Color c, FontWeight? w = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, w ?? FontWeights.Normal, FontStretches.Normal), size, new SolidColorBrush(c), 1.0);

    static void Label(DrawingContext dc, string s, double x, double y, double size, Color c) => dc.DrawText(Text(s, size, c), new Point(x, y));
}
