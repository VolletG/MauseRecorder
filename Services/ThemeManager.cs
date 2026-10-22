using System.Windows;
using System.Windows.Media;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public static class ThemeManager
{
    public static AccentPalette ApplyAccent(string name)
    {
        var palette = AccentPalettes.Get(name);
        var accent = (Color)ColorConverter.ConvertFromString(palette.Hex);
        var soft = (Color)ColorConverter.ConvertFromString(palette.SoftHex);

        Set("AccentColor", accent);
        Set("AccentBrush", Freeze(new SolidColorBrush(accent)));
        Set("AccentSoftBrush", Freeze(new SolidColorBrush(soft)));
        Set("AccentFaintBrush", Freeze(new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B))));
        Set("AccentGlowBrush", Freeze(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B))));
        return palette;
    }

    public static Color CurrentAccent =>
        Application.Current.TryFindResource("AccentColor") is Color color ? color : Color.FromRgb(139, 92, 246);

    private static void Set(string key, object value)
    {
        if (Application.Current is not null)
            Application.Current.Resources[key] = value;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
