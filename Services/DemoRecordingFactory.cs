using MauseRecorder.Models;

namespace MauseRecorder.Services;

public static class DemoRecordingFactory
{
    /// <summary>
    /// A little love letter from your cursor: a heart-shaped mouse path.
    /// </summary>
    public static Recording CreateHeartDemo()
    {
        var screen = ScreenMetrics.VirtualScreen();
        var scale = Math.Min(screen.Width, screen.Height) * 0.017;
        var centerX = screen.Left + screen.Width / 2.0;
        var centerY = screen.Top + screen.Height / 2.0 + screen.Height * 0.03;
        var events = new List<MouseEvent>();
        const int samples = 240;
        const double duration = 7600;

        for (var i = 0; i <= samples; i++)
        {
            var t = i / (double)samples * Math.PI * 2;
            var x = 16 * Math.Pow(Math.Sin(t), 3);
            var y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            var px = (int)Math.Round(centerX + x * scale);
            var py = (int)Math.Round(centerY - y * scale);
            var time = i / (double)samples * duration;

            events.Add(new MouseEvent { Time = time, X = px, Y = py, Type = MouseEventType.Move });

            if (i is > 0 and < samples && i % 48 == 0)
            {
                events.Add(new MouseEvent { Time = time, X = px, Y = py, Type = MouseEventType.LeftDown });
                events.Add(new MouseEvent { Time = time + 80, X = px, Y = py, Type = MouseEventType.LeftUp });
            }
        }

        // Finish with a tiny scroll flourish.
        var last = events[^1];
        events.Add(new MouseEvent
        {
            Time = duration + 250,
            X = last.X,
            Y = last.Y,
            Type = MouseEventType.Wheel,
            WheelDelta = 240
        });

        return new Recording
        {
            Id = "demo-heart",
            Name = LocalizationService.T("S_Demo_name"),
            CreatedAt = DateTime.Now,
            DurationMs = duration + 600,
            ScreenLeft = screen.Left,
            ScreenTop = screen.Top,
            ScreenWidth = screen.Width,
            ScreenHeight = screen.Height,
            Events = events
        };
    }
}
