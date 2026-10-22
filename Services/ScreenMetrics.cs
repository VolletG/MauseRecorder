using MauseRecorder.Interop;

namespace MauseRecorder.Services;

public readonly record struct ScreenMetrics(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public static ScreenMetrics VirtualScreen()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new ScreenMetrics(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            width > 0 ? width : 1920,
            height > 0 ? height : 1080);
    }
}
