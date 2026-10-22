using System.Diagnostics;
using System.Runtime.InteropServices;
using MauseRecorder.Interop;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public sealed class PlaybackOptions
{
    public double Speed { get; init; } = 1.0;
    public bool Loop { get; init; }
    public bool GremlinMode { get; init; }
}

public sealed record PlaybackProgress(double Fraction, int LoopNumber, bool IsComplete);

public sealed class PlaybackService
{
    public event EventHandler? ClickFeedback;

    public async Task PlayAsync(
        Recording recording,
        PlaybackOptions options,
        CancellationToken cancellationToken,
        IProgress<PlaybackProgress>? progress = null)
    {
        if (recording.Events.Count == 0) return;

        var speed = Math.Clamp(options.Speed, 0.25, 5);
        var screen = ScreenMetrics.VirtualScreen();
        var events = recording.Events;
        NativeMethods.TimeBeginPeriod(1);

        try
        {
            for (var loop = 0; ; loop++)
            {
                var clock = Stopwatch.StartNew();
                var lastReport = -100.0;
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var mouseEvent in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DelayUntilAsync(clock, mouseEvent.Time / speed, cancellationToken);

                    var x = mouseEvent.X;
                    var y = mouseEvent.Y;

                    if (options.GremlinMode)
                    {
                        x += Random.Shared.Next(-5, 6);
                        y += Random.Shared.Next(-5, 6);
                    }

                    SendMouseEvent(mouseEvent, x, y, screen);

                    if (mouseEvent.IsClickDown)
                        ClickFeedback?.Invoke(this, EventArgs.Empty);

                    var elapsed = clock.Elapsed.TotalMilliseconds;
                    if (elapsed - lastReport >= 40)
                    {
                        lastReport = elapsed;
                        progress?.Report(new PlaybackProgress(
                            Math.Clamp(elapsed / Math.Max(1, recording.DurationMs), 0, 1), loop, false));
                    }
                }

                if (!options.Loop)
                {
                    progress?.Report(new PlaybackProgress(1, loop, true));
                    break;
                }

                progress?.Report(new PlaybackProgress(1, loop, false));
                await Task.Delay(220, cancellationToken);
            }
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private static void SendMouseEvent(MouseEvent mouseEvent, int x, int y, ScreenMetrics screen)
    {
        var (nx, ny) = Normalize(x, y, screen);

        var flags = mouseEvent.Type switch
        {
            MouseEventType.Move => NativeMethods.MOUSEEVENTF_MOVE,
            MouseEventType.LeftDown => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            MouseEventType.LeftUp => NativeMethods.MOUSEEVENTF_LEFTUP,
            MouseEventType.RightDown => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            MouseEventType.RightUp => NativeMethods.MOUSEEVENTF_RIGHTUP,
            MouseEventType.MiddleDown => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            MouseEventType.MiddleUp => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            MouseEventType.Wheel => NativeMethods.MOUSEEVENTF_WHEEL,
            MouseEventType.XDown => NativeMethods.MOUSEEVENTF_XDOWN,
            MouseEventType.XUp => NativeMethods.MOUSEEVENTF_XUP,
            _ => NativeMethods.MOUSEEVENTF_MOVE
        };

        if (mouseEvent.Type != MouseEventType.Wheel)
            flags |= NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK;

        var data = mouseEvent.Type switch
        {
            MouseEventType.Wheel => unchecked((uint)mouseEvent.WheelDelta),
            MouseEventType.XDown or MouseEventType.XUp => (uint)Math.Max(1, mouseEvent.Button),
            _ => 0u
        };

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = nx,
                dy = ny,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };

        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static (int X, int Y) Normalize(int x, int y, ScreenMetrics screen)
    {
        var width = Math.Max(2, screen.Width);
        var height = Math.Max(2, screen.Height);
        var nx = (int)Math.Round((x - screen.Left) * 65535.0 / (width - 1));
        var ny = (int)Math.Round((y - screen.Top) * 65535.0 / (height - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    private static async Task DelayUntilAsync(Stopwatch clock, double targetMs, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;

            if (remaining > 16)
                await Task.Delay((int)Math.Min(remaining - 8, 30), cancellationToken);
            else if (remaining > 2)
                await Task.Delay(1, cancellationToken);
            else
                Thread.SpinWait(120);
        }
    }
}
