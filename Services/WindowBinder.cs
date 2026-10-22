using System.Diagnostics;
using System.Text;
using MauseRecorder.Interop;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

internal sealed record WindowTargetInfo(
    string Title,
    string ProcessName,
    int Left,
    int Top,
    int Width,
    int Height);

/// <summary>
/// Captures the top-level window under the cursor while recording, and finds /
/// activates it again on replay so takes are bound to the app they belong to.
/// </summary>
internal static class WindowBinder
{
    private static readonly HashSet<string> IgnoredTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager",
        "Windows Input Experience",
        "Microsoft Text Input Application",
        "Taskbar",
        "Start",
        "Search"
    };

    public static WindowTargetInfo? CaptureAt(int x, int y, uint ownProcessId)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT(x, y));
        if (hwnd == IntPtr.Zero) return null;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        NativeMethods.GetWindowThreadProcessId(root, out var pid);
        if (pid == 0 || pid == ownProcessId) return null;
        if (!NativeMethods.IsWindowVisible(root)) return null;
        if (!NativeMethods.GetWindowRect(root, out var rect)) return null;

        var title = GetTitle(root);
        if (string.IsNullOrWhiteSpace(title) || IgnoredTitles.Contains(title)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        return new WindowTargetInfo(title, GetProcessName(pid), rect.Left, rect.Top, width, height);
    }

    public static IntPtr Find(Recording recording)
    {
        if (!recording.HasTarget) return IntPtr.Zero;

        var exact = NativeMethods.FindWindow(null, recording.TargetWindowTitle);
        if (exact != IntPtr.Zero && NativeMethods.IsWindowVisible(exact)) return exact;

        // Titles change (browser tabs, document names) — fall back to the process.
        if (!string.IsNullOrWhiteSpace(recording.TargetProcessName))
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(recording.TargetProcessName))
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        return process.MainWindowHandle;
                }
            }
            catch
            {
                // Process may have exited between enumeration and access.
            }
        }

        return IntPtr.Zero;
    }

    public static void RestoreAndActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        if (NativeMethods.GetForegroundWindow() == hwnd) return;

        // Windows can refuse SetForegroundWindow when the caller is not the
        // current foreground process. Briefly share input state to lift that
        // restriction and make sure the bound window really comes forward.
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread) return;

        NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public static PlaybackWindow? CurrentRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        return new PlaybackWindow(rect.Left, rect.Top, width, height);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0) return "";

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "";
        }
    }
}
