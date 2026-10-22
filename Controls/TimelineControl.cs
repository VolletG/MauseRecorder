using System.Windows;
using System.Windows.Media;
using MauseRecorder.Models;

namespace MauseRecorder.Controls;

/// <summary>
/// A hand-drawn activity waveform with click markers and a playback playhead.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private Recording? _recording;
    private double _progress;

    public Brush AccentBrush { get; set; } = new SolidColorBrush(Color.FromRgb(139, 92, 246));
    public Brush AccentSoftBrush { get; set; } = new SolidColorBrush(Color.FromArgb(55, 139, 92, 246));
    public Brush GridBrush { get; set; } = new SolidColorBrush(Color.FromRgb(32, 36, 46));    public Recording? Recording
    {
        get => _recording;
        set { _recording = value; InvalidateVisual(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public bool IsLive { get; set; }

    public TimelineControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var accentBrush = TryFindResource("AccentBrush") as Brush ?? AccentBrush;
        var gridBrush = TryFindResource("LineBrush") as Brush ?? GridBrush;

        var pad = new Thickness(2, 12, 2, 20);
        var left = pad.Left;
        var top = pad.Top;
        var right = width - pad.Right;
        var bottom = height - pad.Bottom;
        var plotWidth = Math.Max(1, right - left);
        var plotHeight = Math.Max(1, bottom - top);
        var centerY = top + plotHeight / 2;

        var gridPen = new Pen(gridBrush, 1);
        gridPen.Freeze();

        foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
        {
            var y = top + plotHeight * fraction;
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
        }

        var recording = _recording;
        if (recording is null || recording.Events.Count == 0)
        {
            var flat = new Pen(new SolidColorBrush(Color.FromRgb(45, 50, 63)), 1.4)
            {
                DashStyle = new DashStyle([4, 6], 0)
            };
            flat.Freeze();
            dc.DrawLine(flat, new Point(left, centerY), new Point(right, centerY));
            DrawPlayhead(dc, left, right, top, bottom);
            return;
        }

        var duration = Math.Max(1, recording.DurationMs);
        var samples = BuildWaveform(recording, 180);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left, centerY), true, true);
            for (var i = 0; i < samples.Count; i++)
            {
                var x = left + plotWidth * i / (samples.Count - 1.0);
                var amplitude = samples[i] * plotHeight * 0.44;
                ctx.LineTo(new Point(x, centerY - amplitude), true, false);
            }
            for (var i = samples.Count - 1; i >= 0; i--)
            {
                var x = left + plotWidth * i / (samples.Count - 1.0);
                var amplitude = samples[i] * plotHeight * 0.44;
                ctx.LineTo(new Point(x, centerY + amplitude), true, false);
            }
        }
        geometry.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(88, 139, 92, 246),
            Color.FromArgb(10, 139, 92, 246),
            new Point(0, 0.5), new Point(1, 0.5));
        if (accentBrush is SolidColorBrush solid)
        {
            var c = solid.Color;
            fill = new LinearGradientBrush(
                Color.FromArgb(95, c.R, c.G, c.B),
                Color.FromArgb(8, c.R, c.G, c.B),
                new Point(0, 0.5), new Point(1, 0.5));
        }
        fill.Freeze();

        var linePen = new Pen(accentBrush, 1.7);
        linePen.Freeze();

        dc.DrawGeometry(fill, linePen, geometry);

        // Click markers.
        var clicks = recording.Events.Where(e => e.IsClickDown).Take(240).ToList();
        foreach (var click in clicks)
        {
            var x = left + plotWidth * Math.Clamp(click.Time / duration, 0, 1);
            var markerColor = click.Type switch
            {
                MouseEventType.LeftDown => Color.FromRgb(255, 93, 110),
                MouseEventType.RightDown => Color.FromRgb(251, 176, 64),
                MouseEventType.MiddleDown => Color.FromRgb(34, 211, 238),
                _ => Color.FromRgb(163, 230, 53)
            };
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(95, markerColor.R, markerColor.G, markerColor.B)), 1);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, bottom - 11), new Point(x, bottom - 3));

            var dot = new SolidColorBrush(markerColor);
            dot.Freeze();
            dc.DrawEllipse(dot, null, new Point(x, bottom - 2.5), 2.5, 2.5);
        }

        DrawPlayhead(dc, left, right, top, bottom);
    }

    private void DrawPlayhead(DrawingContext dc, double left, double right, double top, double bottom)
    {
        if (_progress <= 0 && !IsLive) return;

        var x = left + (right - left) * Math.Clamp(_progress, 0, 1);
        var glow = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        glow.Freeze();
        dc.DrawRectangle(glow, null, new Rect(x - 2.5, top - 6, 5, bottom - top + 12));

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.3);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, top - 6), new Point(x, bottom + 6));
        dc.DrawEllipse(Brushes.White, null, new Point(x, top - 6), 3, 3);
    }

    private static List<double> BuildWaveform(Recording recording, int bucketCount)
    {
        var buckets = new double[bucketCount];
        var duration = Math.Max(1, recording.DurationMs);
        var lastX = recording.Events[0].X;
        var lastY = recording.Events[0].Y;

        foreach (var e in recording.Events)
        {
            var dx = e.X - lastX;
            var dy = e.Y - lastY;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            lastX = e.X;
            lastY = e.Y;
            if (e.Type != MouseEventType.Move) continue;
            if (distance > 400) continue; // remove teleport-like jumps

            var index = Math.Clamp((int)(e.Time / duration * bucketCount), 0, bucketCount - 1);
            buckets[index] += distance;
        }

        // Smooth the signal.
        var smoothed = new double[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var total = 0.0;
            var weight = 0.0;
            for (var k = -2; k <= 2; k++)
            {
                var index = i + k;
                if (index < 0 || index >= bucketCount) continue;
                var w = 3.0 - Math.Abs(k);
                total += buckets[index] * w;
                weight += w;
            }
            smoothed[i] = weight > 0 ? total / weight : 0;
        }

        var max = smoothed.Max();
        if (max <= 0) max = 1;
        return smoothed.Select(v => Math.Clamp(v / max, 0.025, 1)).ToList();
    }
}
