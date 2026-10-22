using System.Text.Json.Serialization;

namespace MauseRecorder.Models;

public enum MouseEventType
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    Wheel,
    XDown,
    XUp
}

public sealed class MouseEvent
{
    public double Time { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public MouseEventType Type { get; set; }
    public int WheelDelta { get; set; }
    public int Button { get; set; }

    [JsonIgnore]
    public bool IsClickDown => Type is MouseEventType.LeftDown or MouseEventType.RightDown or MouseEventType.MiddleDown or MouseEventType.XDown;

    [JsonIgnore]
    public bool IsClickUp => Type is MouseEventType.LeftUp or MouseEventType.RightUp or MouseEventType.MiddleUp or MouseEventType.XUp;
}

public sealed class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled take";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double DurationMs { get; set; }
    public int ScreenLeft { get; set; }
    public int ScreenTop { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public List<MouseEvent> Events { get; set; } = [];

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public int ClickCount => Events.Count(e => e.IsClickDown);

    [JsonIgnore]
    public int MoveCount => Events.Count(e => e.Type == MouseEventType.Move);

    [JsonIgnore]
    public int ScrollCount => Events.Count(e => e.Type == MouseEventType.Wheel);

    [JsonIgnore]
    public string DurationLabel
    {
        get
        {
            var span = TimeSpan.FromMilliseconds(DurationMs);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds / 100}";
        }
    }

    [JsonIgnore]
    public string DurationShort
    {
        get
        {
            var span = TimeSpan.FromMilliseconds(DurationMs);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                    : $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))}s";
        }
    }

    [JsonIgnore]
    public string CreatedLabel
    {
        get
        {
            var today = DateTime.Today;
            if (CreatedAt.Date == today)
                return $"Today · {CreatedAt:HH:mm}";
            if (CreatedAt.Date == today.AddDays(-1))
                return $"Yesterday · {CreatedAt:HH:mm}";
            return CreatedAt.ToString("MMM d · HH:mm");
        }
    }

    [JsonIgnore]
    public string Summary => $"{ClickCount} clicks · {MoveCount} moves · {ScrollCount} scrolls";

    [JsonIgnore]
    public string ClickLabel => ClickCount == 1 ? "1 click" : $"{ClickCount} clicks";
}
