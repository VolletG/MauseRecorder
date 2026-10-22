using System.Windows;
using System.Windows.Media;
using MauseRecorder.Models;
using MauseRecorder.Services;

namespace MauseRecorder.Controls;

/// <summary>
/// Draws an aggregate heatmap of every recorded click.
/// </summary>
public sealed class HeatmapControl : FrameworkElement
{
    private IReadOnlyList<Recording> _recordings = [];
    private IReadOnlyList<(double X, double Y, int Button)> _clicks = [];

    public HeatmapControl()
    {
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public int TotalClicks => _clicks.Count;

    public void SetData(IReadOnlyList<Recording> recordings)
    {
        _recordings = recordings;
        var clicks = new List<(double X, double Y, int Button)>();

        foreach (var recording in recordings)
        {
            var width = Math.Max(1, recording.ScreenWidth);
            var height = Math.Max(1, recording.ScreenHeight);
            foreach (var e in recording.Events.Where(e => e.IsClickDown))
            {
                var x = (e.X - recording.ScreenLeft) / (double)width;
                var y = (e.Y - recording.ScreenTop) / (double)height;
                if (x is < -0.02 or > 1.02 || y is < -0.02 or > 1.02) continue;
                clicks.Add((Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), (int)e.Type));
            }
        }

        _clicks = clicks;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var screenAspect = 16.0 / 9;
        var width = Math.Min(ActualWidth, ActualHeight * screenAspect);
        var height = width / screenAspect;
        if (height > ActualHeight)
        {
            height = ActualHeight;
            width = height * screenAspect;
        }

        var x = (ActualWidth - width) / 2;
        var y = (ActualHeight - height) / 2;
        var screenRect = new Rect(x, y, width, height);

        var backdrop = new SolidColorBrush(Color.FromRgb(8, 10, 14));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(43, 47, 59)), 1);
        border.Freeze();
        dc.DrawRoundedRectangle(backdrop, border, screenRect, 12, 12);

        dc.PushClip(new RectangleGeometry(screenRect, 12, 12));

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 132, 142, 167)), 0.7);
        gridPen.Freeze();
        for (var i = 1; i < 4; i++)
        {
            var gx = screenRect.Left + screenRect.Width * i / 4;
            var gy = screenRect.Top + screenRect.Height * i / 4;
            dc.DrawLine(gridPen, new Point(gx, screenRect.Top), new Point(gx, screenRect.Bottom));
            dc.DrawLine(gridPen, new Point(screenRect.Left, gy), new Point(screenRect.Right, gy));
        }

        if (_clicks.Count == 0)
        {
            var emptyPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 139, 92, 246)), 1)
            {
                DashStyle = new DashStyle([5, 6], 0)
            };
            emptyPen.Freeze();
            var cx = screenRect.Left + screenRect.Width / 2;
            var cy = screenRect.Top + screenRect.Height / 2;
            dc.DrawEllipse(null, emptyPen, new Point(cx, cy), 44, 44);
            dc.DrawEllipse(null, emptyPen, new Point(cx, cy), 80, 80);
            dc.DrawLine(emptyPen, new Point(cx - 18, cy), new Point(cx + 18, cy));
            dc.DrawLine(emptyPen, new Point(cx, cy - 18), new Point(cx, cy + 18));
            dc.Pop();
            return;
        }

        // Group nearby clicks into cells so thousands of clicks still render fast.
        const int cellCount = 48;
        var cells = new Dictionary<(int X, int Y), int>();
        var perCell = new Dictionary<(int X, int Y), List<int>>();
        foreach (var click in _clicks)
        {
            var key = ((int)(click.X * cellCount), (int)(click.Y * cellCount));
            cells[key] = cells.GetValueOrDefault(key) + 1;
            if (!perCell.TryGetValue(key, out var list))
            {
                list = [];
                perCell[key] = list;
            }
            list.Add(click.Button);
        }

        var max = Math.Max(1, cells.Values.Max());
        var ordered = cells.OrderBy(kv => kv.Value).ToList();

        foreach (var (key, count) in ordered)
        {
            var cx = screenRect.Left + (key.X + 0.5) / cellCount * screenRect.Width;
            var cy = screenRect.Top + (key.Y + 0.5) / cellCount * screenRect.Height;
            var strength = Math.Pow(count / (double)max, 0.55);
            var radius = screenRect.Width * (0.018 + 0.06 * strength);
            var hotColor = strength > 0.62
                ? Color.FromRgb(255, 86, 96)
                : strength > 0.28
                    ? Color.FromRgb(255, 145, 66)
                    : Color.FromRgb(139, 92, 246);

            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(40 + strength * 125), hotColor.R, hotColor.G, hotColor.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(18 + strength * 45), hotColor.R, hotColor.G, hotColor.B), 0.55));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(70 + strength * 150), 255, 245, 245)), null,
                new Point(cx, cy), Math.Max(1.1, radius * 0.035), Math.Max(1.1, radius * 0.035));
        }

        dc.Pop();
    }
}
