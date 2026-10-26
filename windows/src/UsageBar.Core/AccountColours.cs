namespace UsageBar.Core;

/// <summary>
/// An account colour: one shade for dark surfaces (the dark taskbar and flyout), one for light ones, and a badge
/// shade that keeps a white tag readable. The tray and the flyout use the same shade, so the colour you pick is
/// the colour you see everywhere.
/// </summary>
public sealed record ColourPreset(string Id, string Name, string OnDark, string OnLight, string Badge)
{
    public string For(bool dark) => dark ? OnDark : OnLight;
}

/// <summary>
/// Ten account colours chosen to read as small tray bars and numbers on both taskbars (decision 17). The tests
/// hold every preset to measured contrast and keep any two far enough apart to tell at a glance.
/// </summary>
public static class AccountColours
{
    public static readonly IReadOnlyList<ColourPreset> All = new ColourPreset[]
    {
        new("orange", "Orange", "#FF8F4D", "#A94806", "#A94806"),
        new("blue", "Blue", "#6EB3FF", "#1C63C7", "#1C63C7"),
        new("green", "Green", "#4DD08A", "#16794A", "#16794A"),
        new("purple", "Purple", "#B794FF", "#7347D6", "#7347D6"),
        new("pink", "Pink", "#FF7EB8", "#BE2F72", "#BE2F72"),
        new("teal", "Teal", "#3DD1D1", "#0B7580", "#0B7580"),
        new("yellow", "Yellow", "#FFD24D", "#8A6100", "#8A6100"),
        new("red", "Red", "#FF8880", "#C42727", "#C42727"),
        new("lime", "Lime", "#A6E05A", "#46700E", "#46700E"),
        new("grey", "Grey", "#C8C8C8", "#5C5C5C", "#5C5C5C"),
    };

    public static ColourPreset Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    /// <summary>
    /// The preset for an older free-form colour (0.1.x saved hex values): the same colour family by hue, so the
    /// Mac's clay stays orange; washed-out colours become grey.
    /// </summary>
    public static ColourPreset Nearest(string hex)
    {
        if (!Rgb.TryParse(hex, out var r, out var g, out var b)) return All[0];
        var (h, s) = HueSat(r, g, b);
        if (s < 0.15) return Get("grey");
        return All.Where(p => p.Id != "grey").OrderBy(p =>
        {
            Rgb.TryParse(p.OnDark, out var pr, out var pg, out var pb);
            var d = Math.Abs(HueSat(pr, pg, pb).H - h);
            return Math.Min(d, 360 - d);
        }).First();
    }

    static (double H, double S) HueSat(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), c = max - min;
        if (c == 0) return (0, 0);
        var h = max == r ? 60 * (((g - b) / c) % 6) : max == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        var l = (max + min) / 2;
        return ((h + 360) % 360, c / (1 - Math.Abs(2 * l - 1)));
    }

    // MARK: Measures

    static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    static (double R, double G, double B) Lin(string hex)
    {
        Rgb.TryParse(hex, out var r, out var g, out var b);
        return (Linear(r), Linear(g), Linear(b));
    }

    static double Luminance(string hex)
    {
        var (r, g, b) = Lin(hex);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>WCAG contrast ratio between two colours (1 to 21).</summary>
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static (double L, double A, double B) Lab(string hex)
    {
        var (r, g, b) = Lin(hex);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b,
               z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>CIE76 colour difference: about 2 is just noticeable, 20 and up reads as a different colour.</summary>
    public static double Distance(string a, string b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }
}
