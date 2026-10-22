using System.Diagnostics;
using System.Runtime.InteropServices;
using MauseRecorder.Interop;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public readonly record struct PlaybackWindow(int Left, int Top, int Width, int Height);

public sealed class PlaybackOptions
{
    public double Speed { get; init; } = 1.0;
    public bool Loop { get; init; }
    public bool GremlinMode { get; init; }

    /// <summary>Current rect of the window this take is bound to, if it is open.</summary>
    public PlaybackWindow? Window { get; init; }
}

public sealed record PlaybackProgress(double Fraction, int LoopNumber, bool IsComplete);

public sealed class PlaybackService
{
    public event EventHandler? ClickFeedback;

    public Task PlayAsync(
        Recording recording,
        PlaybackOptions options,
        CancellationToken cancellationToken,
        IProgress<PlaybackProgress>? progress = null)
    {
        if (recording.Events.Count == 0) return Task.CompletedTask;

        // Playback runs on a dedicated thread: dispatching every step through the
        // UI thread added scheduling jitter and made the cursor visibly stutter.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                PlayCore(recording, options, cancellationToken, progress);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "MauseRecorder playback",
            Priority = ThreadPriority.AboveNormal
        };

        thread.Start();
        return completion.Task;
    }

    private void PlayCore(
        Recording recording,
        PlaybackOptions options,
        CancellationToken cancellationToken,
        IProgress<PlaybackProgress>? progress)
    {
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
                    DelayUntil(clock, mouseEvent.Time / speed, cancellationToken);

                    var (x, y) = MapToWindow(mouseEvent, recording, options.Window);

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
                Sleep(220, cancellationToken);
            }
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private static (int X, int Y) MapToWindow(MouseEvent mouseEvent, Recording recording, PlaybackWindow? window)
    {
        if (window is not { } current
            || recording.TargetWindowWidth <= 0
            || recording.TargetWindowHeight <= 0)
        {
            return (mouseEvent.X, mouseEvent.Y);
        }

        var recordedLeft = recording.TargetWindowLeft;
        var recordedTop = recording.TargetWindowTop;
        var recordedRight = recordedLeft + recording.TargetWindowWidth;
        var recordedBottom = recordedTop + recording.TargetWindowHeight;

        // Only remap points that were inside the recorded window. Clicks that
        // intentionally landed elsewhere keep their absolute screen position.
        if (mouseEvent.X < recordedLeft || mouseEvent.X > recordedRight ||
            mouseEvent.Y < recordedTop || mouseEvent.Y > recordedBottom)
        {
            return (mouseEvent.X, mouseEvent.Y);
        }

        var x = current.Left + (mouseEvent.X - recordedLeft) * current.Width / (double)recording.TargetWindowWidth;
        var y = current.Top + (mouseEvent.Y - recordedTop) * current.Height / (double)recording.TargetWindowHeight;
        return ((int)Math.Round(x), (int)Math.Round(y));
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

    /// <summary>
    /// High-resolution wait: sleep for the bulk of the gap, then spin for the
    /// last ~1.5 ms so moves land at their recorded timestamps.
    /// </summary>
    private static void DelayUntil(Stopwatch clock, double targetMs, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = targetMs - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;

            if (remaining > 16)
                Thread.Sleep((int)Math.Min(remaining - 8, 30));
            else if (remaining > 1.5)
                Thread.Sleep(1);
            else
                Thread.SpinWait(120);
        }
    }

    private static void Sleep(int milliseconds, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(5);
        }
    }
}
