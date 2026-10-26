using System.Windows;
using System.Windows.Media;

namespace UsageBar;

/// <summary>
/// A usage bar with its pace marker, as on the Mac: the layout box is as tall as the marker (always, so rows line up
/// whether or not there is a marker) and the bar is centred in it.
/// </summary>
internal sealed class PaceBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(PaceBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(nameof(Tick), typeof(double?), typeof(PaceBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(PaceBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(nameof(BarHeight), typeof(double), typeof(PaceBar),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty TickHeightProperty = DependencyProperty.Register(nameof(TickHeight), typeof(double), typeof(PaceBar),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double? Tick { get => (double?)GetValue(TickProperty); set => SetValue(TickProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }
    public double TickHeight { get => (double)GetValue(TickHeightProperty); set => SetValue(TickHeightProperty, value); }

    public PaceBar() => SnapsToDevicePixels = true;

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 100 : available.Width, TickHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = BarHeight;
        var y = (TickHeight - h) / 2;
        var r = h / 2;
        dc.DrawRoundedRectangle(TryFindResource("Track") as Brush, null, new Rect(0, y, w, h), r, r);
        var fill = Math.Clamp(Fraction, 0, 1) * w;
        if (fill > 0) dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, y, fill, h), r, r);
        if (Tick is { } t)
        {
            var x = Math.Max(0, Math.Min(w - 2, w * t - 1));
            dc.DrawRoundedRectangle(TryFindResource("Tick") as Brush, null, new Rect(x, 0, 2, TickHeight), 1, 1);
        }
    }
}

/// <summary>Cost per day for the last 30 days: bars with rounded tops on a baseline.</summary>
internal sealed class CostChart : FrameworkElement
{
    /// <summary>A bar with only its top corners rounded (one shape, so a translucent fill doesn't double up).</summary>
    static Geometry TopRounded(double x, double y, double w, double h, double radius)
    {
        var r = Math.Min(radius, Math.Min(w / 2, h / 2));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, y + h), true, true);
            c.LineTo(new Point(x, y + r), false, false);
            c.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(x + w - r, y), false, false);
            c.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(x + w, y + h), false, false);
        }
        g.Freeze();
        return g;
    }

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<ChartBarVm>), typeof(CostChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(CostChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartBarVm>? Bars { get => (IReadOnlyList<ChartBarVm>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 100 : available.Width, 64);

    protected override void OnRender(DrawingContext dc)
    {
        var bars = Bars ?? Array.Empty<ChartBarVm>();
        const double gap = 3, h = 64;
        var w = ActualWidth;
        if (bars.Count > 0)
        {
            var bw = (w - gap * (bars.Count - 1)) / bars.Count;
            for (var i = 0; i < bars.Count; i++)
            {
                var bh = Math.Min(h, bars[i].Height);
                dc.DrawGeometry(BarBrush, null, TopRounded(i * (bw + gap), h - bh, bw, bh, 2));
            }
        }
        if (TryFindResource("ChartBase") is Brush baseLine) dc.DrawRectangle(baseLine, null, new Rect(0, h - 1, w, 1));
    }
}
