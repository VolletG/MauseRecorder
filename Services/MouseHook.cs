using System.Runtime.InteropServices;
using MauseRecorder.Interop;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public sealed class GlobalMouseEventArgs(int x, int y, MouseEventType type, int wheelDelta, int button, bool isInjected) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public MouseEventType Type { get; } = type;
    public int WheelDelta { get; } = wheelDelta;
    public int Button { get; } = button;
    public bool IsInjected { get; } = isInjected;
}

/// <summary>
/// A WH_MOUSE_LL hook. It never swallows input; it only observes it.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _callback;
    private IntPtr _handle;
    private bool _running;

    public event EventHandler<GlobalMouseEventArgs>? MouseEvent;

    public MouseHook()
    {
        // Keep a strong reference: the OS holds only a native pointer.
        _callback = HookCallback;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;

        _handle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Could not install the global mouse hook (error " +
                                                Marshal.GetLastWin32Error() + ").");

        _running = true;
    }

    public void Stop()
    {
        if (!_running) return;

        if (_handle != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(_handle);

        _handle = IntPtr.Zero;
        _running = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _running)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var message = (int)wParam;
                var injected = (data.flags & NativeMethods.LLMHF_INJECTED) != 0;

                var type = message switch
                {
                    NativeMethods.WM_MOUSEMOVE => (MouseEventType?)MouseEventType.Move,
                    NativeMethods.WM_LBUTTONDOWN => MouseEventType.LeftDown,
                    NativeMethods.WM_LBUTTONUP => MouseEventType.LeftUp,
                    NativeMethods.WM_RBUTTONDOWN => MouseEventType.RightDown,
                    NativeMethods.WM_RBUTTONUP => MouseEventType.RightUp,
                    NativeMethods.WM_MBUTTONDOWN => MouseEventType.MiddleDown,
                    NativeMethods.WM_MBUTTONUP => MouseEventType.MiddleUp,
                    NativeMethods.WM_MOUSEWHEEL => MouseEventType.Wheel,
                    NativeMethods.WM_XBUTTONDOWN => MouseEventType.XDown,
                    NativeMethods.WM_XBUTTONUP => MouseEventType.XUp,
                    _ => null
                };

                if (type is not null)
                {
                    var button = 0;
                    var delta = 0;

                    if (message is NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP)
                        button = (int)(data.mouseData >> 16);
                    else if (message is NativeMethods.WM_MOUSEWHEEL)
                        delta = unchecked((short)(data.mouseData >> 16));

                    MouseEvent?.Invoke(this, new GlobalMouseEventArgs(
                        data.pt.X, data.pt.Y, type.Value, delta, button, injected));
                }
            }
            catch
            {
                // A hook must never throw into the OS callback.
            }
        }

        return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
