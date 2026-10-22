using System.Diagnostics;
using MauseRecorder.Interop;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public sealed class RecorderService : IDisposable
{
    private readonly MouseHook _hook = new();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly List<MouseEvent> _events = [];
    private readonly Stopwatch _clock = new();

    private AppSettings _settings = new();
    private double _lastMoveMs = -1000;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private bool _recording;

    public bool IsRecording => _recording;
    public double ElapsedMs => _recording ? _clock.Elapsed.TotalMilliseconds : 0;
    public int EventCount => _events.Count;
    public int ClickCount { get; private set; }
    public int ScrollCount { get; private set; }

    /// <summary>Live view of the events captured during the current take.</summary>
    public List<MouseEvent> CapturedEvents => _events;

    public event EventHandler<MouseEvent>? EventCaptured;

    public RecorderService()
    {
        _hook.MouseEvent += OnGlobalMouse;
    }

    public void Start(AppSettings settings)
    {
        if (_recording) return;

        _settings = settings.Clone();
        _events.Clear();
        ClickCount = 0;
        ScrollCount = 0;
        _lastMoveMs = -1000;
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _clock.Restart();
        _recording = true;
        _hook.Start();
    }

    public Recording? Stop()
    {
        if (!_recording) return null;

        _recording = false;
        _clock.Stop();
        _hook.Stop();

        if (_events.Count == 0) return null;

        var duration = Math.Max(_clock.Elapsed.TotalMilliseconds, _events[^1].Time);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        return new Recording
        {
            Name = "Untitled take",
            CreatedAt = DateTime.Now,
            DurationMs = Math.Max(1, duration),
            ScreenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            ScreenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            ScreenWidth = width > 0 ? width : 1920,
            ScreenHeight = height > 0 ? height : 1080,
            Events = [.. _events]
        };
    }

    private void OnGlobalMouse(object? sender, GlobalMouseEventArgs e)
    {
        if (!_recording || e.IsInjected) return;

        // Never record interactions with MauseRecorder itself: the HUD stop
        // button should not end up inside your take.
        if (IsOverOwnWindow(e.X, e.Y)) return;

        var now = _clock.Elapsed.TotalMilliseconds;

        switch (e.Type)
        {
            case MouseEventType.Move:
                if (!_settings.CaptureMotion) return;
                if (now - _lastMoveMs < 9) return;
                if (e.X == _lastX && e.Y == _lastY) return;
                _lastMoveMs = now;
                _lastX = e.X;
                _lastY = e.Y;
                break;

            case MouseEventType.Wheel:
                ScrollCount++;
                break;

            case MouseEventType.LeftDown or MouseEventType.RightDown or
                 MouseEventType.MiddleDown or MouseEventType.XDown:
                ClickCount++;
                break;
        }

        var mouseEvent = new MouseEvent
        {
            Time = Math.Round(now, 2),
            X = e.X,
            Y = e.Y,
            Type = e.Type,
            WheelDelta = e.WheelDelta,
            Button = e.Button
        };

        _events.Add(mouseEvent);
        EventCaptured?.Invoke(this, mouseEvent);
    }

    private bool IsOverOwnWindow(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT(x, y));
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
    }

    public void Dispose()
    {
        _hook.Dispose();
    }
}
