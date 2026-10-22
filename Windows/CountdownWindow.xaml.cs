using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MauseRecorder.Interop;
using MauseRecorder.Services;

namespace MauseRecorder.Windows;

public partial class CountdownWindow : Window
{
    public string CancelHotkey { get; set; } = "Ctrl + Alt + R";

    public CountdownWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CancelHint.Text = LocalizationService.F("S_Cancel_hint", CancelHotkey);
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    public async Task RunAsync(int seconds, CancellationToken cancellationToken)
    {
        for (var i = seconds; i >= 1; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NumberText.Text = i.ToString();
            AnimateNumber();
            await Task.Delay(1000, cancellationToken);
        }
    }

    private void AnimateNumber()
    {
        if (NumberText.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        var fade = new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220));
        NumberText.BeginAnimation(OpacityProperty, fade);
    }
}
