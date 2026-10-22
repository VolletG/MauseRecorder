using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MauseRecorder.Controls;
using MauseRecorder.Interop;
using MauseRecorder.Models;
using MauseRecorder.Services;
using MauseRecorder.Windows;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;

namespace MauseRecorder;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyRecord = 0xA001;
    private const int HotkeyPlay = 0xA002;
    private const int HotkeyShow = 0xA003;

    private readonly StorageService _storage;
    private readonly ObservableCollection<Recording> _recordings = [];
    private readonly ICollectionView _archiveView;
    private readonly RecorderService _recorder = new();
    private readonly PlaybackService _playback = new();
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Random _random = new();

    private AppSettings _settings;
    private Recording? _currentRecording;
    private Recording? _liveRecording;
    private OverlayWindow? _overlay;
    private CountdownWindow? _countdown;
    private CancellationTokenSource? _countdownCts;
    private CancellationTokenSource? _playCts;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _hotkeyRecordRegistered;
    private bool _hotkeyPlayRegistered;
    private bool _hotkeyShowRegistered;
    private string? _hotkeyWarning;
    private bool _isPlaying;
    private bool _loadingUi = true;
    private bool _minimizedForRecord;
    private bool _minimizedForPlayback;
    private bool _isClosing;
    private int _confettiVersion;

    public MainWindow(StorageService storage, AppSettings settings)
    {
        _storage = storage;
        _settings = settings;
        _settings.Normalize();

        InitializeComponent();

        _archiveView = CollectionViewSource.GetDefaultView(_recordings);
        _archiveView.Filter = FilterRecording;
        RecordingsList.ItemsSource = _archiveView;

        Toast.RenderTransform = new TranslateTransform(0, 10);
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => TickRecording(), Dispatcher);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.4) };
        _toastTimer.Tick += (_, _) => HideToast();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _storage.SaveSettings(_settings);
        };

        _playback.ClickFeedback += (_, _) =>
        {
            if (_settings.PlayClickSounds) SoundService.PlayClick();
        };

        LoadArchive();
        ApplySettingsToUi();
        _loadingUi = false;

        UpdateSpeedLabels();
        SetCurrentRecording(_currentRecording);
        DataPathText.Text = _storage.RootDirectory;
        UpdateHeatmap();
        UpdateArchiveSummary();
        SetStatusReady("Waiting for a take");

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MainContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        if (MainContent.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        }

        if (_hotkeyWarning is not null)
        {
            Dispatcher.BeginInvoke(new Action(() => ShowToast("Hotkey unavailable", _hotkeyWarning, "!", true)),
                DispatcherPriority.ApplicationIdle);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        RegisterHotkeys();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton is null) return;

        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        WindowFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _ticker.Stop();
        _toastTimer.Stop();
        _saveTimer.Stop();

        if (_recorder.IsRecording)
        {
            var recording = _recorder.Stop();
            if (recording is { Events.Count: > 0 })
            {
                try
                {
                    recording.Name = _storage.NextTakeName(_recordings.Count);
                    _storage.SaveRecording(recording);
                    _settings.LastRecordingId = recording.Id;
                }
                catch
                {
                    // Do not block shutdown on a rare disk error.
                }
            }
        }

        _playCts?.Cancel();
        _countdownCts?.Cancel();
        CloseOverlay();
        CloseCountdown();
        _storage.SaveSettings(_settings);
        UnregisterHotkeys();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _recorder.Dispose();
        base.OnClosing(e);
    }

    // ═══════════════════════════════ Window chrome ═══════════════════════════════

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═══════════════════════════════ Navigation ═══════════════════════════════

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton radio) return;
        var page = radio.Tag?.ToString();
        ShowPage(page);
    }

    private void ShowPage(string? page)
    {
        RecorderPage.Visibility = page == "recorder" ? Visibility.Visible : Visibility.Collapsed;
        ArchivePage.Visibility = page == "archive" ? Visibility.Visible : Visibility.Collapsed;
        HeatmapPage.Visibility = page == "heatmap" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement? visible = page switch
        {
            "archive" => ArchivePage,
            "heatmap" => HeatmapPage,
            "settings" => SettingsPage,
            _ => RecorderPage
        };

        if (visible is not null)
        {
            visible.Opacity = 0;
            visible.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        if (page == "archive")
        {
            _archiveView.Refresh();
            UpdateArchiveEmptyState();
        }
        else if (page == "heatmap")
        {
            UpdateHeatmap();
        }
        else if (page == "settings")
        {
            DataPathText.Text = _storage.RootDirectory;
        }
    }

    // ═══════════════════════════════ Recording ═══════════════════════════════

    private void Record_Click(object sender, RoutedEventArgs e) => ToggleRecording();

    private void NewTake_Click(object sender, RoutedEventArgs e) => ToggleRecording();

    private void Record_ClickForHotkey() => ToggleRecording();

    private async void ToggleRecording()
    {
        try
        {
            if (_isClosing) return;

            if (_countdownCts is not null)
            {
                _countdownCts.Cancel();
                return;
            }

            if (_isPlaying)
            {
                StopPlayback();
                ShowToast("Replay stopped", "Now you can start a fresh take.", "■");
                return;
            }

            if (_recorder.IsRecording)
            {
                StopRecording();
                return;
            }

            await BeginRecordingAsync();
        }
        catch (Exception ex)
        {
            ShowToast("Could not record", ex.Message, "!", true);
        }
    }

    private async Task BeginRecordingAsync()
    {
        if (_settings.MinimizeOnRecord && WindowState != WindowState.Minimized)
        {
            _minimizedForRecord = true;
            WindowState = WindowState.Minimized;
        }

        if (_settings.CountdownSeconds > 0)
        {
            var cts = new CancellationTokenSource();
            _countdownCts = cts;
            _countdown = new CountdownWindow();
            _countdown.Show();
            var cancelled = false;

            try
            {
                await _countdown.RunAsync(_settings.CountdownSeconds, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                cancelled = true;
                ShowToast("Countdown failed", ex.Message, "!", true);
            }
            finally
            {
                CloseCountdown();
                _countdownCts?.Dispose();
                _countdownCts = null;
            }

            if (cancelled)
            {
                RestoreAfterRecording();
                ShowToast("Countdown cancelled", "No clicks were harmed.", "×");
                return;
            }
        }

        if (_isClosing) return;

        try
        {
            _recorder.Start(_settings);
        }
        catch (Exception ex)
        {
            RestoreAfterRecording();
            ShowToast("Could not start", ex.Message, "!", true);
            return;
        }

        var screen = ScreenMetrics.VirtualScreen();
        _liveRecording = new Recording
        {
            Name = "Live take",
            CreatedAt = DateTime.Now,
            DurationMs = 1,
            ScreenLeft = screen.Left,
            ScreenTop = screen.Top,
            ScreenWidth = screen.Width,
            ScreenHeight = screen.Height,
            Events = _recorder.CapturedEvents
        };

        Timeline.IsLive = true;
        Timeline.Recording = _liveRecording;
        Timeline.Progress = 1;
        TimelineTakeName.Text = "Recording live · you're on";
        TimelineEventCount.Text = "0 events";
        StatEvents.Text = "0";
        StatDuration.Text = "00:00";
        StatClicks.Text = "0";

        SetRecordingUi(true);

        if (_settings.ShowRecordingHud)
        {
            _overlay = new OverlayWindow(_recorder);
            _overlay.StopRequested += Overlay_StopRequested;
            _overlay.Show();
        }

        _ticker.Start();
        TickRecording();
    }

    private void StopRecording()
    {
        if (!_recorder.IsRecording) return;

        _ticker.Stop();
        var recording = _recorder.Stop();
        CloseOverlay();
        SetRecordingUi(false);

        if (recording is null)
        {
            Timeline.IsLive = false;
            Timeline.Progress = 0;
            RestoreAfterRecording();
            ShowToast("Nothing captured", "The mouse was suspiciously quiet. Try a little longer.", "·");
            SetStatusReady("Waiting for a take");
            return;
        }

        recording.Name = _storage.NextTakeName(_recordings.Count);
        recording.CreatedAt = DateTime.Now;
        _storage.SaveRecording(recording);
        _recordings.Insert(0, recording);
        _settings.LastRecordingId = recording.Id;
        QueueSaveSettings();

        SetCurrentRecording(recording);
        UpdateHeatmap();
        UpdateArchiveSummary();
        RestoreAfterRecording();
        SetStatusReady("Last take · " + recording.Name);
        ShowToast("Take saved", BuildTakeSummary(recording), "✓");
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButtonText.Text = recording ? "Stop recording" : "Start recording";
        HeroPlayButton.IsEnabled = !recording;
        TimelinePlayButton.IsEnabled = !recording;

        if (recording)
        {
            SetStatus("RECORDING", Brush("DangerBrush"), "00:00 · 0 events", true);
        }
        else
        {
            Timeline.IsLive = false;
            Timeline.Progress = 0;
            if (!_isPlaying) SetStatusReady("Ready for another take");
        }
    }

    private void TickRecording()
    {
        if (!_recorder.IsRecording) return;

        var elapsed = _recorder.ElapsedMs;
        var events = _recorder.EventCount;
        var clicks = _recorder.ClickCount;

        StatEvents.Text = events.ToString("N0");
        StatClicks.Text = clicks.ToString("N0");
        StatDuration.Text = FormatDuration(elapsed);
        TimelineEventCount.Text = $"{events:N0} events";
        TimelineTakeName.Text = $"Recording live · {FormatDuration(elapsed)}";
        StatusDetail.Text = $"{FormatDuration(elapsed)} · {events:N0} events";

        if (_liveRecording is not null)
        {
            _liveRecording.DurationMs = Math.Max(1, elapsed);
            Timeline.Recording = _liveRecording;
            Timeline.Progress = 1;
        }
    }

    private void Overlay_StopRequested(object? sender, EventArgs e) => StopRecording();

    private void SetCurrentRecording(Recording? recording)
    {
        _currentRecording = recording;
        Timeline.IsLive = false;
        Timeline.Progress = 0;
        Timeline.Recording = recording;

        if (recording is null)
        {
            StatEvents.Text = "0";
            StatDuration.Text = "00:00";
            StatClicks.Text = "0";
            TimelineTakeName.Text = "Nothing recorded yet";
            TimelineEventCount.Text = "0 events";
            HeroPlayButton.ToolTip = "Play the last take";
            return;
        }

        StatEvents.Text = recording.Events.Count.ToString("N0");
        StatDuration.Text = FormatDuration(recording.DurationMs);
        StatClicks.Text = recording.ClickCount.ToString("N0");
        TimelineTakeName.Text = $"{recording.Name} · {recording.CreatedLabel}";
        TimelineEventCount.Text = $"{recording.Events.Count:N0} events";
        HeroPlayButton.ToolTip = "Play " + recording.Name;
    }

    private void RestoreAfterRecording()
    {
        if (!_minimizedForRecord) return;
        _minimizedForRecord = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ═══════════════════════════════ Playback ═══════════════════════════════

    private async void PlayLast_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback();
            return;
        }

        await StartPlaybackFlowAsync(_currentRecording);
    }

    private async void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Recording recording }) return;
        SetCurrentRecording(recording);
        await StartPlaybackFlowAsync(recording);
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopPlayback();
            return;
        }

        if (_recorder.IsRecording)
        {
            ShowToast("Still recording", "Stop the take before summoning the demo.", "·");
            return;
        }

        var demo = DemoRecordingFactory.CreateHeartDemo();
        SetCurrentRecording(demo);
        ShowToast("Demo mode", "A heart-shaped cursor path is about to happen.", "✦");
        await StartPlaybackAsync(demo);
    }

    private async Task StartPlaybackFlowAsync(Recording? recording)
    {
        if (_isClosing) return;

        if (_recorder.IsRecording)
        {
            ShowToast("Recording in progress", "Stop the take first, then hit replay.", "·");
            return;
        }

        if (_countdownCts is not null)
        {
            _countdownCts.Cancel();
            return;
        }

        if (_isPlaying)
        {
            StopPlayback();
            return;
        }

        if (recording is null || recording.Events.Count == 0)
        {
            var demo = DemoRecordingFactory.CreateHeartDemo();
            SetCurrentRecording(demo);
            ShowToast("Demo mode", "No takes yet, so here's a heart-shaped cursor path.", "✦");
            await StartPlaybackAsync(demo);
            return;
        }

        await StartPlaybackAsync(recording);
    }

    private async Task StartPlaybackAsync(Recording recording)
    {
        if (_isPlaying || recording.Events.Count == 0) return;

        SetCurrentRecording(recording);
        _isPlaying = true;
        var cts = new CancellationTokenSource();
        _playCts = cts;
        var token = cts.Token;

        if (_settings.MinimizeOnPlayback && WindowState != WindowState.Minimized)
        {
            _minimizedForPlayback = true;
            WindowState = WindowState.Minimized;
        }

        SetPlaybackUi(true);
        var completed = false;
        var failed = false;

        try
        {
            var options = new PlaybackOptions
            {
                Speed = _settings.PlaybackSpeed,
                Loop = _settings.LoopPlayback,
                GremlinMode = _settings.GremlinMode
            };

            var progress = new Progress<PlaybackProgress>(p =>
            {
                if (!_isPlaying) return;
                Timeline.Progress = p.Fraction;
                StatusDetail.Text = p.LoopNumber > 0
                    ? $"{recording.Name} · loop {p.LoopNumber + 1}"
                    : recording.Name;
            });

            await _playback.PlayAsync(recording, options, token, progress);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch (Exception ex)
        {
            failed = true;
            ShowToast("Playback hiccup", ex.Message, "!", true);
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts)) _playCts = null;
            cts.Dispose();
            _isPlaying = false;
            SetPlaybackUi(false);
            RestoreAfterPlayback();

            if (!_isClosing && !failed)
            {
                if (completed)
                {
                    ShowToast("That's a wrap", $"{recording.Name} · {FormatDuration(recording.DurationMs)} · {_settings.PlaybackSpeed:0.##}× speed", "✓");
                    if (_settings.PartyMode)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(PlayConfetti), DispatcherPriority.Background);
                    }
                }
                else
                {
                    ShowToast("Replay stopped", "Your cursor is free.", "■");
                }
            }
        }
    }

    private void StopPlayback() => _playCts?.Cancel();

    private void SetPlaybackUi(bool playing)
    {
        HeroPlayText.Text = playing ? "Stop replay" : "Play last take";
        HeroPlayIcon.Text = playing ? "■" : "▶";
        TimelinePlayText.Text = playing ? "Stop" : "Play take";
        TimelinePlayIcon.Text = playing ? "■" : "▶";

        if (playing)
        {
            Timeline.Progress = 0;
            SetStatus("PLAYING", Brush("AccentBrush"), _currentRecording?.Name ?? "Replay", true);
        }
        else
        {
            Timeline.Progress = 0;
            SetStatusReady(_currentRecording is null ? "Waiting for a take" : "Last take · " + _currentRecording.Name);
        }
    }

    private void RestoreAfterPlayback()
    {
        if (!_minimizedForPlayback) return;
        _minimizedForPlayback = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ═══════════════════════════════ Status & toast ═══════════════════════════════

    private void SetStatus(string text, Brush brush, string detail, bool pulse)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
        StatusDetail.Text = detail;

        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;

        if (pulse)
        {
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
    }

    private void SetStatusReady(string detail)
    {
        SetStatus("READY", (Brush)FindResource("MutedBrush"), detail, false);
    }

    private void ShowToast(string title, string message, string glyph = "✓", bool error = false)
    {
        if (_isClosing) return;

        Toast.Visibility = Visibility.Visible;
        ToastTitle.Text = title;
        ToastText.Text = message;
        ToastGlyph.Text = error ? "!" : glyph;
        ToastIcon.Background = error ? Hex("#2A1016") : (Brush)FindResource("AccentFaintBrush");
        ToastGlyph.Foreground = error ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("AccentBrush");
        Toast.Opacity = 0;

        if (Toast.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    private string BuildTakeSummary(Recording recording)
    {
        var achievement = recording.ClickCount >= 200
            ? "Achievement unlocked: Click Goblin."
            : recording.DurationMs >= 60_000
                ? "Achievement unlocked: Marathon Mouse."
                : recording.ScrollCount >= 20
                    ? "Achievement unlocked: Scroll Lord."
                    : "Achievement unlocked: Fresh Take.";

        return $"{recording.DurationShort} · {recording.Events.Count:N0} events · {recording.ClickCount:N0} clicks. {achievement}";
    }

    // ═══════════════════════════════ Archive ═══════════════════════════════

    private void LoadArchive()
    {
        foreach (var recording in _storage.LoadRecordings())
            _recordings.Add(recording);

        _currentRecording = _settings.LastRecordingId is not null
            ? _recordings.FirstOrDefault(r => r.Id == _settings.LastRecordingId)
            : null;
        _currentRecording ??= _recordings.FirstOrDefault();
        _settings.LastRecordingId = _currentRecording?.Id;
    }

    private bool FilterRecording(object item)
    {
        if (item is not Recording recording) return false;
        var query = ArchiveSearch?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return true;

        return recording.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || recording.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
               || recording.CreatedLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ArchiveSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || SearchPlaceholder is null) return;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(ArchiveSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        _archiveView?.Refresh();
        UpdateArchiveEmptyState();
    }

    private void UpdateArchiveSummary()
    {
        ArchiveNavCount.Text = _recordings.Count.ToString();
        var clicks = _recordings.Sum(r => r.ClickCount);
        var totalMs = _recordings.Sum(r => r.DurationMs);
        ArchiveSummary.Text = $"{_recordings.Count} take{(_recordings.Count == 1 ? "" : "s")} · {clicks:N0} clicks";
        ArchiveTotalTime.Text = $"{FormatTotalDuration(totalMs)} recorded";
        UpdateArchiveEmptyState();
    }

    private void UpdateArchiveEmptyState()
    {
        if (ArchiveEmpty is null) return;

        var visibleCount = _archiveView.Cast<object>().Count();
        if (visibleCount > 0)
        {
            ArchiveEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        ArchiveEmpty.Visibility = Visibility.Visible;
        var searching = !string.IsNullOrWhiteSpace(ArchiveSearch.Text);
        ArchiveEmptyTitle.Text = searching ? "No takes match that" : "Your archive is a blank canvas";
        ArchiveEmptyText.Text = searching
            ? "Try a different search — or make a brand new take."
            : "Record a take and it will land here automatically. No cloud, no account, no nonsense.";
    }

    private async void RecordingsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (RecordingsList.SelectedItem is Recording recording)
        {
            SetCurrentRecording(recording);
            await StartPlaybackFlowAsync(recording);
        }
    }

    private void RenameRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Recording recording }) return;

        var name = DialogWindow.Prompt(this, "Rename take", "Give this masterpiece a name.", recording.Name, "Save name");
        if (string.IsNullOrWhiteSpace(name)) return;

        recording.Name = name.Trim();
        _storage.SaveRecording(recording, recording.FilePath);

        var index = _recordings.IndexOf(recording);
        if (index >= 0)
        {
            _recordings.RemoveAt(index);
            _recordings.Insert(index, recording);
        }

        if (ReferenceEquals(_currentRecording, recording))
            SetCurrentRecording(recording);

        _archiveView.Refresh();
        UpdateArchiveSummary();
        ShowToast("Renamed", $"Now called “{recording.Name}”.", "✎");
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Recording recording }) return;

        if (!DialogWindow.Confirm(this, "Delete this take?",
                $"“{recording.Name}” will be removed from your archive. This cannot be undone.", "Delete take", true))
            return;

        if (_isPlaying) StopPlayback();
        _storage.DeleteRecording(recording);
        _recordings.Remove(recording);

        if (_settings.LastRecordingId == recording.Id)
        {
            _settings.LastRecordingId = null;
            QueueSaveSettings();
        }

        if (ReferenceEquals(_currentRecording, recording))
            SetCurrentRecording(_recordings.FirstOrDefault());

        UpdateArchiveSummary();
        UpdateHeatmap();
        ShowToast("Take deleted", "A tiny funeral for a tiny take.", "✕");
    }

    private void ClearArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            ShowToast("Still recording", "Stop the take before clearing the archive.", "·");
            return;
        }

        if (_recordings.Count == 0)
        {
            ShowToast("Already clean", "There are no takes to delete.", "·");
            return;
        }

        if (!DialogWindow.Confirm(this, "Burn the whole archive?",
                $"All {_recordings.Count} takes will be permanently deleted from this computer.", "Delete everything", true))
            return;

        if (_isPlaying) StopPlayback();
        _storage.ClearRecordings();
        _recordings.Clear();
        _settings.LastRecordingId = null;
        QueueSaveSettings();
        SetCurrentRecording(null);
        UpdateArchiveSummary();
        UpdateHeatmap();
        ShowToast("Archive cleared", "Zero takes. Infinite possibilities.", "✓");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _storage.OpenDataFolder();

    // ═══════════════════════════════ Heatmap ═══════════════════════════════

    private void UpdateHeatmap()
    {
        Heatmap.SetData(_recordings);

        var clicks = new List<(Recording Recording, MouseEvent Event)>();
        foreach (var recording in _recordings)
        {
            foreach (var mouseEvent in recording.Events.Where(ev => ev.IsClickDown))
                clicks.Add((recording, mouseEvent));
        }

        HeatTotal.Text = clicks.Count.ToString("N0");
        HeatTakesLabel.Text = _recordings.Count == 0
            ? "No data yet"
            : $"across {_recordings.Count} take{(_recordings.Count == 1 ? "" : "s")}";
        HeatTakeCount.Text = $"{_recordings.Count} take{(_recordings.Count == 1 ? "" : "s")}";
        HeatmapEmpty.Visibility = clicks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (clicks.Count == 0)
        {
            HeatHotspot.Text = "—";
            HeatButton.Text = "—";
            HeatLeftLabel.Text = "L 0%";
            HeatRightLabel.Text = "R 0%";
            HeatLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            HeatRightColumn.Width = new GridLength(1, GridUnitType.Star);
            RoastText.Text = GenerateRoast();
            return;
        }

        var left = 0;
        var right = 0;
        var top = 0;
        var bottom = 0;
        var leftButtons = 0;
        var rightButtons = 0;
        var middleButtons = 0;
        var otherButtons = 0;

        foreach (var (recording, mouseEvent) in clicks)
        {
            var nx = (mouseEvent.X - recording.ScreenLeft) / (double)Math.Max(1, recording.ScreenWidth);
            var ny = (mouseEvent.Y - recording.ScreenTop) / (double)Math.Max(1, recording.ScreenHeight);

            if (nx < 0.5) left++; else right++;
            if (ny < 0.5) top++; else bottom++;

            switch (mouseEvent.Type)
            {
                case MouseEventType.LeftDown: leftButtons++; break;
                case MouseEventType.RightDown: rightButtons++; break;
                case MouseEventType.MiddleDown: middleButtons++; break;
                default: otherButtons++; break;
            }
        }

        var total = clicks.Count;
        var leftRatio = left / (double)total;
        var rightRatio = right / (double)total;
        HeatLeftLabel.Text = $"L {leftRatio * 100:0}%";
        HeatRightLabel.Text = $"R {rightRatio * 100:0}%";
        HeatLeftColumn.Width = new GridLength(Math.Max(0.01, leftRatio), GridUnitType.Star);
        HeatRightColumn.Width = new GridLength(Math.Max(0.01, rightRatio), GridUnitType.Star);

        var horizontal = left >= right ? "Left" : "Right";
        var vertical = top >= bottom ? "top" : "bottom";
        HeatHotspot.Text = $"{horizontal} {vertical}";

        var favourite = new[]
        {
            ("Left click", leftButtons),
            ("Right click", rightButtons),
            ("Middle click", middleButtons),
            ("Side button", otherButtons)
        }.OrderByDescending(item => item.Item2).First();
        HeatButton.Text = favourite.Item1;

        RoastText.Text = GenerateRoast();
    }

    private string GenerateRoast()
    {
        var clicks = _recordings.Sum(r => r.ClickCount);
        var scrolls = _recordings.Sum(r => r.ScrollCount);
        var moves = _recordings.Sum(r => r.MoveCount);
        var totalMs = _recordings.Sum(r => r.DurationMs);

        var right = 0;
        var left = 0;
        foreach (var recording in _recordings)
        {
            foreach (var e in recording.Events.Where(ev => ev.IsClickDown))
            {
                var x = e.X - recording.ScreenLeft;
                if (x < recording.ScreenWidth / 2.0) left++; else right++;
            }
        }

        var lines = new List<string>();
        if (clicks == 0)
        {
            lines.Add("No clicks yet. Your cursor is still an innocent little arrow.");
            lines.Add("Record a take and I'll have opinions about your mouse habits.");
            lines.Add("A blank heatmap is just a canvas waiting for chaos.");
            return lines[_random.Next(lines.Count)];
        }

        if (clicks >= 500) lines.Add("Certified click goblin. Your mouse deserves dental insurance.");
        else if (clicks >= 120) lines.Add("That's a lot of clicking. Blink once in a while, legend.");
        if (right > left * 1.8) lines.Add("Right-click energy. You have opinions and a context menu.");
        else if (left > right * 1.8) lines.Add("Left-click loyalist. Predictable, reliable, slightly feral.");
        if (scrolls >= 30) lines.Add("You scroll like the answer is hiding at the bottom of the internet.");
        if (moves > clicks * 40 && clicks > 0) lines.Add("So much movement, so few clicks. The cursor equivalent of pacing.");
        if (totalMs > 5 * 60_000) lines.Add("Five minutes of raw mouse footage. Spielberg is calling.");
        if (_recordings.Count >= 10) lines.Add("Ten takes in the archive? This isn't a habit anymore, it's a lifestyle.");

        lines.Add("Your cursor has range. Confusing, but impressive.");
        lines.Add("Suspiciously intentional clicking. I respect it.");
        lines.Add("The mouse is not the problem. The mouse is never the problem.");
        lines.Add("This heatmap says 'productivity'. The timeline says 'side quests'.");

        return lines[_random.Next(lines.Count)];
    }

    private void Roast_Click(object sender, RoutedEventArgs e)
    {
        RoastText.Text = GenerateRoast();
    }

    private void CopyStats_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var clicks = _recordings.Sum(r => r.ClickCount);
            var totalMs = _recordings.Sum(r => r.DurationMs);
            var builder = new StringBuilder();
            builder.AppendLine("MauseRecorder — all-time stats");
            builder.AppendLine("────────────────────────────");
            builder.AppendLine($"Takes:        {_recordings.Count}");
            builder.AppendLine($"Clicks:       {clicks:N0}");
            builder.AppendLine($"Recorded:     {FormatTotalDuration(totalMs)}");
            builder.AppendLine($"Hot zone:     {HeatHotspot.Text}");
            builder.AppendLine($"Favourite:    {HeatButton.Text}");
            builder.AppendLine();
            builder.AppendLine("Made with MauseRecorder · mouse automation, but make it pretty.");
            Clipboard.SetText(builder.ToString());
            ShowToast("Stats copied", "Your cursor résumé is on the clipboard.", "⧉");
        }
        catch (Exception ex)
        {
            ShowToast("Clipboard said no", ex.Message, "!", true);
        }
    }

    private void ExportHeatmap_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export your heatmap card",
            FileName = $"mauserecorder-heatmap-{DateTime.Now:yyyyMMdd}.png",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            ShareRoot.UpdateLayout();
            var width = ShareRoot.ActualWidth;
            var height = ShareRoot.ActualHeight;
            if (width < 10 || height < 10)
            {
                ShowToast("Export failed", "The heatmap is not laid out yet. Try again.", "!", true);
                return;
            }

            const double scale = 2;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width * scale),
                (int)Math.Ceiling(height * scale),
                96 * scale,
                96 * scale,
                PixelFormats.Pbgra32);
            bitmap.Render(ShareRoot);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);

            ShowToast("Card exported", "Your heatmap is now a very shareable PNG.", "↓");
        }
        catch (Exception ex)
        {
            ShowToast("Export failed", ex.Message, "!", true);
        }
    }

    // ═══════════════════════════════ Settings ═══════════════════════════════

    private void ApplySettingsToUi()
    {
        _loadingUi = true;
        try
        {
            SpeedSlider.Value = _settings.PlaybackSpeed;
            SettingsSpeedSlider.Value = _settings.PlaybackSpeed;
            LoopToggle.IsChecked = _settings.LoopPlayback;
            SettingsLoopToggle.IsChecked = _settings.LoopPlayback;
            ClickSoundToggle.IsChecked = _settings.PlayClickSounds;
            PartyToggle.IsChecked = _settings.PartyMode;
            GremlinToggle.IsChecked = _settings.GremlinMode;
            MotionToggle.IsChecked = _settings.CaptureMotion;
            MinimizeRecordToggle.IsChecked = _settings.MinimizeOnRecord;
            MinimizePlayToggle.IsChecked = _settings.MinimizeOnPlayback;
            HudToggle.IsChecked = _settings.ShowRecordingHud;
            TopmostToggle.IsChecked = _settings.AlwaysOnTop;

            CountdownOff.IsChecked = _settings.CountdownSeconds == 0;
            Countdown3.IsChecked = _settings.CountdownSeconds == 3;
            Countdown5.IsChecked = _settings.CountdownSeconds == 5;

            AccentViolet.IsChecked = _settings.Accent == "Violet";
            AccentCyan.IsChecked = _settings.Accent == "Cyan";
            AccentLime.IsChecked = _settings.Accent == "Lime";
            AccentSunset.IsChecked = _settings.Accent == "Sunset";

            ThemeManager.ApplyAccent(_settings.Accent);
            Timeline.InvalidateVisual();
            Topmost = _settings.AlwaysOnTop;
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ReadToggles()
    {
        _settings.LoopPlayback = LoopToggle.IsChecked == true;
        _settings.PlayClickSounds = ClickSoundToggle.IsChecked == true;
        _settings.PartyMode = PartyToggle.IsChecked == true;
        _settings.GremlinMode = GremlinToggle.IsChecked == true;
        _settings.CaptureMotion = MotionToggle.IsChecked == true;
        _settings.MinimizeOnRecord = MinimizeRecordToggle.IsChecked == true;
        _settings.MinimizeOnPlayback = MinimizePlayToggle.IsChecked == true;
        _settings.ShowRecordingHud = HudToggle.IsChecked == true;
        _settings.AlwaysOnTop = TopmostToggle.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
    }

    private void SettingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        ReadToggles();
        QueueSaveSettings();
    }

    private void LoopToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;

        var value = (sender as CheckBox)?.IsChecked == true;
        _settings.LoopPlayback = value;
        _loadingUi = true;
        LoopToggle.IsChecked = value;
        SettingsLoopToggle.IsChecked = value;
        _loadingUi = false;
        QueueSaveSettings();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => HandleSpeedChanged(e.NewValue);

    private void SettingsSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => HandleSpeedChanged(e.NewValue);

    private void HandleSpeedChanged(double value)
    {
        if (_loadingUi) return;
        var snapped = Math.Clamp(Math.Round(value * 4, MidpointRounding.AwayFromZero) / 4, 0.25, 3);
        _settings.PlaybackSpeed = snapped;

        _loadingUi = true;
        SpeedSlider.Value = snapped;
        SettingsSpeedSlider.Value = snapped;
        _loadingUi = false;

        UpdateSpeedLabels();
        QueueSaveSettings();
    }

    private void UpdateSpeedLabels()
    {
        var text = $"{_settings.PlaybackSpeed:0.##}×";
        SpeedLabel.Text = text;
        SettingsSpeedLabel.Text = text;
    }

    private void Countdown_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        if (sender is RadioButton { Tag: not null } radio && int.TryParse(radio.Tag.ToString(), out var seconds))
        {
            _settings.CountdownSeconds = seconds;
            QueueSaveSettings();
        }
    }

    private void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        if (sender is RadioButton { Tag: not null } radio)
        {
            _settings.Accent = radio.Tag.ToString() ?? "Violet";
            ThemeManager.ApplyAccent(_settings.Accent);
            Timeline.InvalidateVisual();
            QueueSaveSettings();
        }
    }

    private void QueueSaveSettings()
    {
        if (_isClosing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Confirm(this, "Reset everything?",
                "Settings will return to their defaults. Your recordings stay exactly where they are.", "Reset settings"))
            return;

        var last = _settings.LastRecordingId;
        _settings = new AppSettings { LastRecordingId = last };
        ApplySettingsToUi();
        UpdateSpeedLabels();
        _storage.SaveSettings(_settings);
        ShowToast("Settings reset", "Back to factory-fresh chaos.", "↻");
    }

    // ═══════════════════════════════ Misc ═══════════════════════════════

    private void StarGithub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.RepositoryUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast("Could not open GitHub", ex.Message, "!", true);
        }
    }

    private void PlayConfetti()
    {
        if (_isClosing || WindowState == WindowState.Minimized) return;

        var width = ConfettiCanvas.ActualWidth;
        var height = ConfettiCanvas.ActualHeight;
        if (width < 10) width = MainContent.ActualWidth;
        if (height < 10) height = MainContent.ActualHeight;

        ConfettiCanvas.Children.Clear();
        _confettiVersion++;
        var version = _confettiVersion;
        var accent = ThemeManager.CurrentAccent;
        var palette = new[]
        {
            accent,
            Color.FromRgb(0xFF, 0x56, 0x68),
            Color.FromRgb(0x22, 0xD3, 0xEE),
            Color.FromRgb(0xA3, 0xE6, 0x35),
            Color.FromRgb(0xFB, 0xB0, 0x40),
            Color.FromRgb(0xF4, 0xF5, 0xF8)
        };

        for (var i = 0; i < 74; i++)
        {
            var color = palette[_random.Next(palette.Length)];
            var shape = new Shapes.Rectangle
            {
                Width = _random.Next(4, 10),
                Height = _random.Next(7, 15),
                Fill = new SolidColorBrush(color),
                RadiusX = 1.5,
                RadiusY = 1.5,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Opacity = 0.95
            };

            Canvas.SetLeft(shape, _random.NextDouble() * width);
            Canvas.SetTop(shape, -20 - _random.NextDouble() * 150);

            var translate = new TranslateTransform();
            var rotate = new RotateTransform(_random.Next(0, 360));
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(translate);
            shape.RenderTransform = group;

            ConfettiCanvas.Children.Add(shape);

            var duration = TimeSpan.FromSeconds(1.6 + _random.NextDouble() * 1.4);
            var delay = TimeSpan.FromMilliseconds(_random.Next(0, 420));
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, height + 120, duration)
                {
                    BeginTime = delay,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, _random.Next(-540, 900), duration) { BeginTime = delay });

            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.72)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            shape.BeginAnimation(OpacityProperty, fade);
        }

        _ = ClearConfettiAsync(version);
    }

    private async Task ClearConfettiAsync(int version)
    {
        await Task.Delay(4200);
        if (_isClosing) return;
        Dispatcher.Invoke(() =>
        {
            if (_confettiVersion == version)
                ConfettiCanvas.Children.Clear();
        });
    }

    // ═══════════════════════════════ Hotkeys ═══════════════════════════════

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;

        _hotkeyRecordRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyRecord,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x52);
        _hotkeyPlayRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyPlay,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x50);
        _hotkeyShowRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyShow,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x48);

        var failed = new List<string>();
        if (!_hotkeyRecordRegistered) failed.Add("Ctrl+Alt+R");
        if (!_hotkeyPlayRegistered) failed.Add("Ctrl+Alt+P");
        if (!_hotkeyShowRegistered) failed.Add("Ctrl+Alt+H");

        if (failed.Count > 0)
            _hotkeyWarning = $"{string.Join(", ", failed)} already taken by another app. The buttons still work.";
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_hotkeyRecordRegistered) NativeMethods.UnregisterHotKey(_hwnd, HotkeyRecord);
        if (_hotkeyPlayRegistered) NativeMethods.UnregisterHotKey(_hwnd, HotkeyPlay);
        if (_hotkeyShowRegistered) NativeMethods.UnregisterHotKey(_hwnd, HotkeyShow);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case HotkeyRecord:
                handled = true;
                Dispatcher.BeginInvoke(new Action(Record_ClickForHotkey), DispatcherPriority.Input);
                break;
            case HotkeyPlay:
                handled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isPlaying) StopPlayback();
                    else _ = StartPlaybackFlowAsync(_currentRecording);
                }), DispatcherPriority.Input);
                break;
            case HotkeyShow:
                handled = true;
                ToggleWindowVisibility();
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleWindowVisibility()
    {
        if (WindowState == WindowState.Minimized)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
        else if (IsActive)
        {
            WindowState = WindowState.Minimized;
        }
        else
        {
            Activate();
        }
    }

    // ═══════════════════════════════ Helpers ═══════════════════════════════

    private void CloseOverlay()
    {
        if (_overlay is null) return;
        _overlay.StopRequested -= Overlay_StopRequested;
        _overlay.Close();
        _overlay = null;
    }

    private void CloseCountdown()
    {
        if (_countdown is null) return;
        _countdown.Close();
        _countdown = null;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static SolidColorBrush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static string FormatDuration(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string FormatTotalDuration(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}h {time.Minutes}m";
        if (time.TotalMinutes >= 1)
            return $"{(int)time.TotalMinutes}m {time.Seconds}s";
        return $"{Math.Max(0, (int)Math.Round(time.TotalSeconds))}s";
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
