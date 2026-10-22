using System.Windows;
using System.Windows.Threading;
using MauseRecorder.Services;

namespace MauseRecorder.Windows;

public partial class OverlayWindow : Window
{
    private readonly RecorderService _recorder;
    private readonly DispatcherTimer _timer;

    public event EventHandler? StopRequested;

    public OverlayWindow(RecorderService recorder)
    {
        _recorder = recorder;
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 4;
        Top = area.Bottom - Height - 4;
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        var elapsed = TimeSpan.FromMilliseconds(_recorder.ElapsedMs);
        TimerText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        EventsText.Text = $"{_recorder.EventCount:N0} events · {_recorder.ClickCount} clicks";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }
}
