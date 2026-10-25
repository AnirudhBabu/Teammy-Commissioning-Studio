using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Teammy.Domain;

namespace Teammy.Desktop;

/// <summary>Small native vector chart. Values come only from the selected persisted simulation run.</summary>
public sealed class TelemetryChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IEnumerable<TelemetrySample>), typeof(TelemetryChart),
        new FrameworkPropertyMetadata(Array.Empty<TelemetrySample>(), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush PlotBackground = CreateBrush("#0D1823");
    private static readonly Brush GridBrush = CreateBrush("#25364A");
    private static readonly Brush LabelBrush = CreateBrush("#A7B7CC");
    private static readonly Brush SpeedBrush = CreateBrush("#7CE0C4");
    private static readonly Brush AreaBrush = CreateBrush("#203F3D");

    public IEnumerable<TelemetrySample> Samples
    {
        get => (IEnumerable<TelemetrySample>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 100 || ActualHeight < 70)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(PlotBackground, null, new Rect(0, 0, ActualWidth, ActualHeight), 7, 7);
        var samples = Samples?.OrderBy(sample => sample.Seconds).ToArray() ?? [];
        var plot = new Rect(49, 19, ActualWidth - 67, ActualHeight - 48);
        var peak = samples.Length == 0 ? 3000 : Math.Max(1, samples.Max(sample => sample.SpeedRpm));
        var yMax = Math.Ceiling(peak / 500) * 500;
        var duration = samples.Length == 0 ? 10 : Math.Max(1, samples.Max(sample => sample.Seconds));
        for (var line = 0; line < 4; line++)
        {
            var y = plot.Bottom - plot.Height * line / 3;
            drawingContext.DrawLine(new Pen(GridBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            DrawLabel(drawingContext, (yMax * line / 3).ToString("0", CultureInfo.InvariantCulture), 8, y - 8, 11);
        }

        DrawLabel(drawingContext, "rpm", plot.Left + 4, 2, 11);
        DrawLabel(drawingContext, "0 s", plot.Left, plot.Bottom + 9, 11);
        DrawLabel(drawingContext, $"{duration:0.#} s", plot.Right - 32, plot.Bottom + 9, 11);
        if (samples.Length < 2)
        {
            DrawLabel(drawingContext, "Run a simulation to inspect the speed profile", plot.Left + 35, plot.Top + plot.Height / 2 - 8, 11);
            return;
        }

        var points = samples.Select(sample => new Point(plot.Left + plot.Width * sample.Seconds / duration,
            plot.Bottom - plot.Height * Math.Clamp(sample.SpeedRpm / yMax, 0, 1))).ToArray();
        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(points[0].X, plot.Bottom), true, true);
            geometry.PolyLineTo(points, true, false);
            geometry.LineTo(new Point(points[^1].X, plot.Bottom), true, false);
        }

        area.Freeze();
        drawingContext.DrawGeometry(AreaBrush, null, area);
        var lineGeometry = new StreamGeometry();
        using (var geometry = lineGeometry.Open())
        {
            geometry.BeginFigure(points[0], false, false);
            geometry.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }

        lineGeometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(SpeedBrush, 2), lineGeometry);
    }

    private void DrawLabel(DrawingContext context, string text, double x, double y, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

    private static Brush CreateBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
