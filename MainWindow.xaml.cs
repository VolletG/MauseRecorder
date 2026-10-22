using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MauseRecorder.Controls;
using MauseRecorder.Interop;
using MauseRecorder.Models;
using MauseRecorder.Services;
using MauseRecorder.Windows;
using Shapes = System.Windows.Shapes;

namespace MauseRecorder;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyRecord = 0xA001;
    private const int HotkeyPlay = 0xA002;
    private const int HotkeyShow = 0xA003;

    private static string T(string key) => LocalizationService.T(key);
    private static string F(string key, params object[] args) => LocalizationService.F(key, args);
    private static string P(string prefix, int count) => LocalizationService.Plural(prefix, count);

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
    private Hotkey _hotkeyRecord = Hotkey.DefaultRecord;
    private Hotkey _hotkeyPlay = Hotkey.DefaultPlay;
    private Hotkey _hotkeyShow = Hotkey.DefaultShow;
    private string? _capturingHotkey;
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
        PreviewKeyDown += MainWindow_PreviewKeyDown;

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
        UpdateArchiveSummary();
        SetStatusReady(T("S_Waiting_for_a_take"));

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
            Dispatcher.BeginInvoke(new Action(() => ShowToast(T("S_Hotkey_unavailable"), _hotkeyWarning, "!", true)),
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
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement? visible = page switch
        {
            "archive" => ArchivePage,
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
                ShowToast(T("S_Replay_stopped"), T("S_Replay_stopped_body"), "■");
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
            ShowToast(T("S_Could_not_record"), ex.Message, "!", true);
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
            _countdown = new CountdownWindow { CancelHotkey = _hotkeyRecord.Format() };
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
                ShowToast(T("S_Countdown_failed"), ex.Message, "!", true);
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
                ShowToast(T("S_Countdown_cancelled"), T("S_Countdown_cancelled_body"), "×");
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
            ShowToast(T("S_Could_not_start"), ex.Message, "!", true);
            return;
        }

        var screen = ScreenMetrics.VirtualScreen();
        _liveRecording = new Recording
        {
            Name = T("S_Live_take"),
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
        TimelineTakeName.Text = F("S_Recording_live", T("S_Youre_on"));
        TimelineEventCount.Text = T("S_0_events");
        StatEvents.Text = "0";
        StatDuration.Text = "00:00";
        StatClicks.Text = "0";

        SetRecordingUi(true);

        if (_settings.ShowRecordingHud)
        {
            _overlay = new OverlayWindow(_recorder, _hotkeyRecord.Format());
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
            ShowToast(T("S_Nothing_captured"), T("S_Nothing_captured_body"), "·");
            SetStatusReady(T("S_Waiting_for_a_take"));
            return;
        }

        recording.Name = _storage.NextTakeName(_recordings.Count);
        recording.CreatedAt = DateTime.Now;
        _storage.SaveRecording(recording);
        _recordings.Insert(0, recording);
        _settings.LastRecordingId = recording.Id;
        QueueSaveSettings();

        SetCurrentRecording(recording);
        UpdateArchiveSummary();
        RestoreAfterRecording();
        SetStatusReady(F("S_Last_take", recording.Name));
        ShowToast(T("S_Take_saved"), BuildTakeSummary(recording), "✓");
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButtonText.Text = recording ? T("S_Stop_recording") : T("S_Start_recording");
        HeroPlayButton.IsEnabled = !recording;
        TimelinePlayButton.IsEnabled = !recording;

        if (recording)
        {
            SetStatus(T("S_RECORDING"), Brush("DangerBrush"), $"00:00 · 0 {T("S_events")}", true);
        }
        else
        {
            Timeline.IsLive = false;
            Timeline.Progress = 0;
            if (!_isPlaying) SetStatusReady(T("S_Ready_for_another_take"));
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
        TimelineEventCount.Text = F("S_EventsCount", events);
        TimelineTakeName.Text = F("S_Recording_live", FormatDuration(elapsed));
        StatusDetail.Text = $"{FormatDuration(elapsed)} · {events:N0} {T("S_events")}";

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
            TimelineTakeName.Text = T("S_Nothing_recorded_yet");
            TimelineEventCount.Text = T("S_0_events");
            HeroPlayButton.ToolTip = T("S_Play_last_take");
            return;
        }

        StatEvents.Text = recording.Events.Count.ToString("N0");
        StatDuration.Text = FormatDuration(recording.DurationMs);
        StatClicks.Text = recording.ClickCount.ToString("N0");
        TimelineTakeName.Text = $"{recording.Name} · {recording.CreatedLabel}";
        TimelineEventCount.Text = F("S_EventsCount", recording.Events.Count);
        HeroPlayButton.ToolTip = F("S_Play_named", recording.Name);
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
            ShowToast(T("S_Still_recording"), T("S_Still_recording_body"), "·");
            return;
        }

        var demo = DemoRecordingFactory.CreateHeartDemo();
        SetCurrentRecording(demo);
        ShowToast(T("S_Demo_mode"), T("S_Demo_heart"), "✦");
        await StartPlaybackAsync(demo);
    }

    private async Task StartPlaybackFlowAsync(Recording? recording)
    {
        if (_isClosing) return;

        if (_recorder.IsRecording)
        {
            ShowToast(T("S_Recording_in_progress"), T("S_Recording_in_progress_body"), "·");
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
            ShowToast(T("S_Demo_mode"), T("S_Demo_no_takes"), "✦");
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

        SetPlaybackUi(true);
        var completed = false;
        var failed = false;
        PlaybackWindow? targetWindow = null;

        try
        {
            // Bring the bound window forward while MauseRecorder is still the
            // foreground app; minimising first would make the OS block us.
            targetWindow = await ActivateTargetWindowAsync(recording, token);

            if (_settings.MinimizeOnPlayback && WindowState != WindowState.Minimized)
            {
                _minimizedForPlayback = true;
                WindowState = WindowState.Minimized;
            }

            var options = new PlaybackOptions
            {
                Speed = _settings.PlaybackSpeed,
                Loop = _settings.LoopPlayback,
                GremlinMode = _settings.GremlinMode,
                Window = targetWindow
            };

            var progress = new Progress<PlaybackProgress>(p =>
            {
                if (!_isPlaying) return;
                Timeline.Progress = p.Fraction;
                StatusDetail.Text = p.LoopNumber > 0
                    ? $"{recording.Name} · {F("S_Loop_label", p.LoopNumber + 1)}"
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
            ShowToast(T("S_Playback_hiccup"), ex.Message, "!", true);
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
                    ShowToast(T("S_Thats_a_wrap"), $"{recording.Name} · {FormatDuration(recording.DurationMs)} · {_settings.PlaybackSpeed:0.##}× speed", "✓");
                    if (_settings.PartyMode)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(PlayConfetti), DispatcherPriority.Background);
                    }
                }
                else
                {
                    ShowToast(T("S_Replay_stopped"), T("S_Cursor_is_free"), "■");
                }
            }
        }
    }

    private static async Task<PlaybackWindow?> ActivateTargetWindowAsync(Recording recording, CancellationToken token)
    {
        if (!recording.HasTarget) return null;

        var hwnd = WindowBinder.Find(recording);
        if (hwnd == IntPtr.Zero) return null;

        WindowBinder.RestoreAndActivate(hwnd);
        await Task.Delay(140, token);
        return WindowBinder.CurrentRect(hwnd);
    }

    private void StopPlayback() => _playCts?.Cancel();

    private void SetPlaybackUi(bool playing)
    {
        HeroPlayText.Text = playing ? T("S_Stop_replay") : T("S_Play_last_take");
        HeroPlayIcon.Text = playing ? "■" : "▶";
        TimelinePlayText.Text = playing ? T("S_Stop") : T("S_Play_take");
        TimelinePlayIcon.Text = playing ? "■" : "▶";

        if (playing)
        {
            Timeline.Progress = 0;
            SetStatus(T("S_PLAYING"), Brush("AccentBrush"), _currentRecording?.Name ?? T("S_Replay"), true);
        }
        else
        {
            Timeline.Progress = 0;
            SetStatusReady(_currentRecording is null ? T("S_Waiting_for_a_take") : F("S_Last_take", _currentRecording.Name));
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
        SetStatus(T("S_READY"), (Brush)FindResource("MutedBrush"), detail, false);
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
            ? T("S_Achievement_click_goblin")
            : recording.DurationMs >= 60_000
                ? T("S_Achievement_marathon")
                : recording.ScrollCount >= 20
                    ? T("S_Achievement_scroll_lord")
                    : T("S_Achievement_fresh_take");

        return $"{recording.DurationShort} · {recording.Events.Count:N0} {T("S_events")} · {recording.ClickCount:N0} {P("S_click", recording.ClickCount)}. {achievement}";
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
        ArchiveSummary.Text = $"{_recordings.Count} {P("S_take", _recordings.Count)} · {clicks:N0} {P("S_click", clicks)}";
        ArchiveTotalTime.Text = F("S_recorded", FormatTotalDuration(totalMs));
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
        ArchiveEmptyTitle.Text = searching ? T("S_No_takes_match_that") : T("S_Your_archive_is_a_blank_canvas");
        ArchiveEmptyText.Text = searching
            ? T("S_No_takes_match_body")
            : T("S_Record_a_take_and_it_will_land_here_automaticall");
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

        var name = DialogWindow.Prompt(this, T("S_Rename_take"), T("S_Rename_take_body"), recording.Name, T("S_Save_name"));
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
        ShowToast(T("S_Renamed"), F("S_Renamed_body", recording.Name), "✎");
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Recording recording }) return;

        if (!DialogWindow.Confirm(this, T("S_Delete_this_take"),
                F("S_Delete_take_body", recording.Name), T("S_Delete_take"), true))
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
        ShowToast(T("S_Take_deleted"), T("S_Take_deleted_body"), "✕");
    }

    private void ClearArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            ShowToast(T("S_Still_recording"), T("S_Still_recording_archive"), "·");
            return;
        }

        if (_recordings.Count == 0)
        {
            ShowToast(T("S_Already_clean"), T("S_Already_clean_body"), "·");
            return;
        }

        if (!DialogWindow.Confirm(this, T("S_Burn_the_whole_archive"),
                F("S_Burn_archive_body", _recordings.Count), T("S_Delete_everything"), true))
            return;

        if (_isPlaying) StopPlayback();
        _storage.ClearRecordings();
        _recordings.Clear();
        _settings.LastRecordingId = null;
        QueueSaveSettings();
        SetCurrentRecording(null);
        UpdateArchiveSummary();
        ShowToast(T("S_Archive_cleared"), T("S_Archive_cleared_body"), "✓");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _storage.OpenDataFolder();

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

            _hotkeyRecord = Hotkey.Parse(_settings.HotkeyRecord) ?? Hotkey.DefaultRecord;
            _hotkeyPlay = Hotkey.Parse(_settings.HotkeyPlay) ?? Hotkey.DefaultPlay;
            _hotkeyShow = Hotkey.Parse(_settings.HotkeyShow) ?? Hotkey.DefaultShow;
            LangEnglish.IsChecked = LocalizationService.Language == LocalizationService.English;
            LangRussian.IsChecked = LocalizationService.Language == LocalizationService.Russian;
            UpdateHotkeyUi();

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
        if (!DialogWindow.Confirm(this, T("S_Reset_everything"),
                T("S_Reset_everything_body"), T("S_Reset_settings")))
            return;

        var last = _settings.LastRecordingId;
        _settings = new AppSettings { LastRecordingId = last };
        LocalizationService.Apply(_settings.Language);
        ApplySettingsToUi();
        UpdateSpeedLabels();
        _storage.SaveSettings(_settings);
        ReRegisterHotkeys();
        RefreshLocalizedTexts();
        ShowToast(T("S_Settings_reset"), T("S_Settings_reset_body"), "↻");
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
            ShowToast(T("S_Could_not_open_github"), ex.Message, "!", true);
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
            _hotkeyRecord.Modifiers | NativeMethods.MOD_NOREPEAT, _hotkeyRecord.VirtualKey);
        _hotkeyPlayRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyPlay,
            _hotkeyPlay.Modifiers | NativeMethods.MOD_NOREPEAT, _hotkeyPlay.VirtualKey);
        _hotkeyShowRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyShow,
            _hotkeyShow.Modifiers | NativeMethods.MOD_NOREPEAT, _hotkeyShow.VirtualKey);

        var failed = new List<string>();
        if (!_hotkeyRecordRegistered) failed.Add(_hotkeyRecord.Format());
        if (!_hotkeyPlayRegistered) failed.Add(_hotkeyPlay.Format());
        if (!_hotkeyShowRegistered) failed.Add(_hotkeyShow.Format());

        _hotkeyWarning = failed.Count == 0
            ? null
            : LocalizationService.F("S_Hotkey_taken", string.Join(", ", failed));
    }

    private void ReRegisterHotkeys()
    {
        UnregisterHotkeys();
        RegisterHotkeys();
        UpdateHotkeyUi();
    }

    private void UpdateHotkeyUi()
    {
        QuickStartHotkey.Text = _hotkeyRecord.Format();
        TipHotkey.Text = _hotkeyRecord.Format();
        HeaderHotkey.Text = _hotkeyRecord.Format();
        HotkeyRecordText.Text = _hotkeyRecord.Format();
        HotkeyPlayText.Text = _hotkeyPlay.Format();
        HotkeyShowText.Text = _hotkeyShow.Format();
        RecordButton.ToolTip = LocalizationService.F("S_Start_stop_recording_hotkey", _hotkeyRecord.Format());
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;

        _capturingHotkey = action;
        var target = action switch
        {
            "Record" => HotkeyRecordText,
            "Play" => HotkeyPlayText,
            _ => HotkeyShowText
        };

        target.Text = LocalizationService.T("S_Press_keys");
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey is null) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) || key == Key.None) return;

        var hotkey = Hotkey.FromKey(key, Keyboard.Modifiers);
        if (!hotkey.IsValid)
        {
            ShowToast(LocalizationService.T("S_Add_a_modifier"),
                LocalizationService.T("S_Add_a_modifier_body"), "!", true);
            return;
        }

        if (IsHotkeyTaken(hotkey, _capturingHotkey))
        {
            ShowToast(LocalizationService.T("S_Hotkey_in_use"),
                LocalizationService.T("S_Hotkey_in_use_body"), "!", true);
            return;
        }

        switch (_capturingHotkey)
        {
            case "Record":
                _hotkeyRecord = hotkey;
                _settings.HotkeyRecord = hotkey.Format();
                break;
            case "Play":
                _hotkeyPlay = hotkey;
                _settings.HotkeyPlay = hotkey.Format();
                break;
            default:
                _hotkeyShow = hotkey;
                _settings.HotkeyShow = hotkey.Format();
                break;
        }

        _capturingHotkey = null;
        EnableHotkeyButtons();
        _storage.SaveSettings(_settings);
        ReRegisterHotkeys();

        if (_hotkeyWarning is not null)
            ShowToast(LocalizationService.T("S_Hotkey_unavailable"), _hotkeyWarning, "!", true);
    }

    private bool IsHotkeyTaken(Hotkey candidate, string action) => action switch
    {
        "Record" => candidate.Equals(_hotkeyPlay) || candidate.Equals(_hotkeyShow),
        "Play" => candidate.Equals(_hotkeyRecord) || candidate.Equals(_hotkeyShow),
        _ => candidate.Equals(_hotkeyRecord) || candidate.Equals(_hotkeyPlay)
    };

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void CancelHotkeyCapture()
    {
        _capturingHotkey = null;
        EnableHotkeyButtons();
        UpdateHotkeyUi();
    }

    private void EnableHotkeyButtons()
    {
        HotkeyRecordButton.IsEnabled = true;
        HotkeyPlayButton.IsEnabled = true;
        HotkeyShowButton.IsEnabled = true;
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        if (sender is not RadioButton { Tag: string language }) return;
        if (language == LocalizationService.Language && language == _settings.Language) return;

        _settings.Language = language;
        LocalizationService.Apply(language);
        _storage.SaveSettings(_settings);
        UpdateHotkeyUi();
        RefreshLocalizedTexts();
    }

    /// <summary>Re-applies every string that is written from code (not from XAML).</summary>
    private void RefreshLocalizedTexts()
    {
        if (_isPlaying)
        {
            HeroPlayText.Text = LocalizationService.T("S_Stop_replay");
            TimelinePlayText.Text = LocalizationService.T("S_Stop");
            SetStatus(LocalizationService.T("S_PLAYING"), (Brush)FindResource("AccentBrush"),
                _currentRecording?.Name ?? LocalizationService.T("S_Replay"), true);
        }
        else
        {
            HeroPlayText.Text = LocalizationService.T("S_Play_last_take");
            TimelinePlayText.Text = LocalizationService.T("S_Play_take");

            if (_liveRecording is not null)
            {
                SetStatus(LocalizationService.T("S_RECORDING"), (Brush)FindResource("DangerBrush"),
                    $"{FormatDuration(_recorder.ElapsedMs)} · {_recorder.EventCount:N0} {LocalizationService.T("S_events")}", true);
            }
            else if (_currentRecording is null)
            {
                SetStatusReady(LocalizationService.T("S_Waiting_for_a_take"));
            }
            else
            {
                SetStatusReady(LocalizationService.F("S_Last_take", _currentRecording.Name));
            }
        }

        RecordButtonText.Text = _liveRecording is not null
            ? LocalizationService.T("S_Stop_recording")
            : LocalizationService.T("S_Start_recording");

        if (_liveRecording is null && !_isPlaying)
            SetCurrentRecording(_currentRecording);

        UpdateArchiveSummary();
        UpdateSpeedLabels();
        DataPathText.Text = _storage.RootDirectory;
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
