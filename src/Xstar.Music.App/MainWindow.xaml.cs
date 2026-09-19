using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Xstar.Music.App.Visualizer;
using Xstar.Music.App.ThreeD;
using Xstar.Music.Core;

namespace Xstar.Music.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IThreeDActions
{
    private readonly LibraryScanner _scanner = new();
    private MusicDatabase _database = null!;
    private readonly IPlayerEngine _player = CreateEngine();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ClockController? _discClock;
    private double _discClockSpeed;
    private ImageSource? _discArtwork;
    private readonly ObservableCollection<SongRow> _songs = new();
    private readonly List<string> _folders = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanDebounce;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private bool _scanning;
    private CancellationTokenSource? _scanCts;
    private int _currentIndex = -1;
    private bool _draggingProgress;
    private bool _restoredOnce;
    private PlaybackMode _playbackMode = PlaybackMode.Sequential;
    private string? _restoredPath;
    private List<string> _restoredQueue = new();
    private long _restoredPositionMs;
    private string _view = "songs";
    private string? _page; // null = album, otherwise "playlist" / "lyrics"
    private readonly List<LyricLine> _lyricLines = new();
    private readonly List<TextBlock> _lyricBlocks = new();
    private readonly List<TextBlock> _cdLyricBlocks = new();
    private bool _cdLyrics;
    private double _lyricsFontSize = 14;
    private string _lyricFontFamily = "";
    private Color? _lyricActiveColor;
    private Color? _lyricInactiveColor;
    private int _lastLyricIndex = -1;
    private bool _dimLyrics = true;
    private bool _discRotation = true;
    private string _displayMode = "normal";
    private double _discSize = 1.2;
    private double _discSpeed = 26;
    // CD mode: user zoom of the disc inside the (fixed) window. 1.0 = fills the window.
    private double _cdZoom = 1.0;
    private const double CdZoomMin = 0.1;
    private const double CdZoomMax = 1.0;
    private const double CdZoomStep = 1.12;
    private double _windowOpacity = 1.0;
    private bool _hideTitleBrand;
    private Effect? _npBlurEffect;
    private const double FrameCornerRadius = 10;
    private bool _reduceMotion;
    private string _themeName = "system";
    private string _themePreset = "deepspace";
    private HashSet<string> _favoriteIds = new(StringComparer.Ordinal);
    private Dictionary<string, long> _historyTicks = new(StringComparer.Ordinal);
    private List<(int Id, string Name)> _playlists = new();
    private readonly Dictionary<int, HashSet<string>> _playlistSongs = new();
    private readonly JamendoProvider _jamendo = new();
    private readonly GdStudioProvider _gdStudio = new();
    private readonly AudiusProvider _audius = new();
    private readonly OnlineMusicService _online;
    private readonly List<SongRow> _onlineResults = new();
    private CancellationTokenSource? _onlineSearch;
    private bool _onlineMode;
    private string _onlineProvider = OnlineMusicService.AllProviders;
    private bool _suppressOnlineSourceEvent;
    private int _onlineSearchGeneration;
    private string _jamendoClientId = "";
    private string _gdSource = "netease";
    private int _gdBitrate = 320;
    private string _audiusAppName = "XstarMusic";
    private string _downloadFolder = "";
    private Song? _currentOnline;
    private bool _suppressPlaybackError;

    private int _sortMode;

    // EQ state
    private bool _eqEnabled;
    private readonly double[] _eqGains = new double[10];
    private readonly List<Slider> _eqSliders = new();
    private readonly List<TextBlock> _eqGainTexts = new();
    private ToggleButton? _eqEnableToggle;
    private bool _suppressEqSave;

    // Visualizer state
    private readonly VisualizerEngine _visualizerEngine;
    private string _visualizerStyle = "stardust";
    private bool _visualizerShowInfo = true;
    private readonly DispatcherTimer _vizControlsTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private bool _vizControlsVisible;
    private bool _vizLyrics;
    private readonly List<TextBlock> _vizLyricBlocks = new();

    // 3D mode (WebView2 + Three.js)
    private readonly ThreeDHost _threeDHost;
    private readonly Dictionary<string, string?> _threeDCardArt = new(StringComparer.Ordinal);
    private string _threeDLibraryKind = "albums";
    private string _threeDQuality = "balanced";
    private string _threeDShelfMode = "side";
    private string _threeDShelfPresence = "always";
    private string _threeDCameraMode = "dynamic";
    private int _threeDCoverPreset;
    private bool _threeDBloom = true;
    private bool _threeDLyrics = true;
    private double _threeDShelfSize = 1.0;
    private double _threeDShelfOffsetX;
    private double _threeDShelfOffsetY;
    private double _threeDShelfAngleY;
    private double _threeDShelfOpacity = 1.0;
    private string _threeDBackgroundColor = "#05060A";
    private string _threeDBackgroundImage = "";
    private double _threeDParticleSize = 1.0;
    private double _threeDGlow = 1.0;
    private double _threeDParticleCount = 1.0;
    private int _threeDParticleColorMode = 1;
    private string _threeDParticleColor = "";
    private string? _threeDBackgroundData;
    private string _threeDBackgroundDataKey = "";

    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic", "settings.json");
    private readonly OnlineLyricsProvider _lyricsProvider = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic", "lyrics"));
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XstarMusic");
    private TaskbarIcon? _tray;
    private bool _exitRequested;
    private HwndSource? _source;
    private const int WmHotkey = 0x0312;
    private static readonly (int Id, uint Vk)[] MediaKeys = { (1, 0xB3), (2, 0xB1), (3, 0xB0), (4, 0xB2) };

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmcpDoNotRound = 1;
    private const int DwmcpRound = 2;
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    /* ===== borderless-window sizing ===== */

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    public MainWindow()
    {
        InitializeComponent();
        SongList.ItemsSource = _songs;
        _online = new OnlineMusicService(new IOnlineMusicProvider[] { _gdStudio, _audius, _jamendo });
        PopulateOnlineSources();
        _visualizerEngine = new VisualizerEngine(_player);
        _threeDHost = new ThreeDHost(ThreeDWebView, _player, this);

        _player.Opened += (_, _) => Dispatcher.Invoke(() =>
        {
            UpdateDuration();
            var duration = _player.Snapshot.Duration;
            if (_restoredPositionMs > 0 && duration > TimeSpan.Zero)
            {
                _player.Seek(TimeSpan.FromMilliseconds(Math.Min(_restoredPositionMs, duration.TotalMilliseconds)));
                _restoredPositionMs = 0;
            }
        });
        _player.Ended += (_, _) => Dispatcher.Invoke(AdvanceAfterEnded);
        _player.Failed += (_, message) => Dispatcher.Invoke(() =>
        {
            PlayGlyphText.Text = "\uE768";
            if (_suppressPlaybackError) return;
            MessageBox.Show($"无法播放此文件：{message}", "播放失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        _timer.Tick += (_, _) => UpdateProgress();
        Loaded += async (_, _) => await LoadAsync();
        SourceInitialized += OnSourceInitialized;
        InitializeTray();
        Closing += OnClosing;
        ThemeManager.Changed += OnThemeChanged;
        DiscGrooves.Source = BuildGrooveImage(340);
        DiscLabelTexture.Source = BuildLabelTextureImage(140);
        UpdateDiscRotation();
        ApplyDisplayMode();
        StateChanged += (_, _) => UpdateFrameClip();
        WindowFrame.SizeChanged += (_, _) => UpdateFrameClip();
        VisualizerView.SizeChanged += VisualizerView_SizeChanged;
        _vizControlsTimer.Tick += (_, _) => HideVisualizerControls();
        PlaybackBar.MouseEnter += (_, _) => { if (IsVisualizerMode) _vizControlsTimer.Stop(); };
        PlaybackBar.MouseLeave += (_, _) => { if (IsVisualizerMode) _vizControlsTimer.Start(); };
        UpdateFrameClip();
    }

    private bool IsVisualizerMode => string.Equals(_displayMode, "visualizer", StringComparison.OrdinalIgnoreCase);

    private void VisualizerView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 10 && e.NewSize.Height > 10)
        {
            int w = (int)Math.Round(e.NewSize.Width);
            int h = (int)Math.Round(e.NewSize.Height);
            _visualizerEngine.SetBitmapSize(w, h);
            VisualizerImage.Source = _visualizerEngine.Bitmap;
        }
    }

    private void OnThemeChanged()
    {
        UpdateNowPlayingGradient();
        UpdateDiscLabel(_discArtwork, ThemeManager.AccentColor);
        UpdateLyricHighlight(force: true);
        // ApplicationThemeManager repaints the window background right after this, so re-assert
        // the fully transparent window surface (window background + composition target).
        EnsureTransparentWindowBackground();
        // Code-built UI captures theme brushes at creation time, so re-apply the ones that live
        // outside the dynamic-resource tree: the title-bar backdrop and the settings panels.
        SyncTitleBarBackdrop();
        if (SettingsOverlay.Visibility == Visibility.Visible) BuildSettings(_settingsSection);
    }

    /// <summary>
    /// Pushes the current accent into the visualizer and, while the visualizer mode is active,
    /// repaints the window frame with the accent-tinted backdrop. The backdrop stays dark (the
    /// renderers need it for additive glow) but its hue follows the selected theme / cover colour.
    /// Keeping the frame opaque in visualizer mode is also what makes the title bar strip
    /// hit-testable again — the layered window manager skips fully transparent pixels.
    /// </summary>
    private void SyncVisualizerTheme()
    {
        var accent = ThemeManager.AccentColor;
        var bg = RendererUtils.ThemedBackground(accent);
        _visualizerEngine.AccentColor = accent;
        _visualizerEngine.BackgroundColor = bg;

        var brush = new SolidColorBrush(bg);
        brush.Freeze();
        _vizSurfaceBrush = brush;

        if (string.Equals(_displayMode, "visualizer", StringComparison.OrdinalIgnoreCase))
            WindowFrame.Background = brush;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveSettings();
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        ThemeManager.Changed -= OnThemeChanged;
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _visualizerEngine.Dispose();
        _threeDHost.Dispose();
        if (_source is not null)
        {
            foreach (var key in MediaKeys) UnregisterHotKey(_source.Handle, key.Id);
            _source.RemoveHook(WndProc);
        }
        _tray?.Dispose();
        _player.Dispose();
        _database.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        foreach (var key in MediaKeys) RegisterHotKey(handle, key.Id, 0, key.Vk);
        EnsureTransparentWindowBackground();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            // A WindowStyle=None window would otherwise maximize over the taskbar ("fullscreen").
            // Clamp the maximized size/position to the monitor work area instead.
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
            }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WmHotkey) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case 1: Play_Click(this, new RoutedEventArgs()); break;
            case 2: Previous_Click(this, new RoutedEventArgs()); break;
            case 3: Next_Click(this, new RoutedEventArgs()); break;
            case 4: _player.Stop(); PlayGlyphText.Text = "\uE768"; break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    private void InitializeTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("显示主窗口", (_, _) => ShowMainWindow()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("上一首", (_, _) => Previous_Click(this, new RoutedEventArgs())));
        menu.Items.Add(CreateMenuItem("播放 / 暂停", (_, _) => Play_Click(this, new RoutedEventArgs())));
        menu.Items.Add(CreateMenuItem("下一首", (_, _) => Next_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出", (_, _) => { _exitRequested = true; Close(); }));
        _tray = new TaskbarIcon
        {
            IconSource = LoadAppIcon(),
            ToolTipText = "XSTAR Music",
            ContextMenu = menu
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    private static MenuItem CreateMenuItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void ShowMainWindow()
    {
        if (_windowOpacity <= 0.001)
        {
            ApplyWindowOpacity(1.0);
            SaveSettings();
            if (SettingsOverlay.Visibility == Visibility.Visible) BuildSettings(_settingsSection);
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Shows or hides the title-bar app icon and "XSTAR" caption.</summary>
    private void ApplyTitleBrand()
    {
        TitleBarControl.Title = _hideTitleBrand ? string.Empty : "XSTAR";
        TitleBarControl.Icon = _hideTitleBrand
            ? null
            : new Wpf.Ui.Controls.ImageIcon
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute))
            };
    }

    private void ApplyWindowOpacity(double value)
    {
        _windowOpacity = value;

        if (value <= 0.001)
        {
            Opacity = 1.0;
            SetClickThrough(false);
            UpdatePerformanceMode(1.0);
            Dispatcher.BeginInvoke(new Action(() => { if (_windowOpacity <= 0.001 && IsVisible) Hide(); }));
            return;
        }

        Opacity = Math.Clamp(value, 0.0, 1.0);
        SetClickThrough(false);
        UpdatePerformanceMode(value);
        if (!IsVisible) Show();
    }

    /// <summary>
    /// The window opts into WPF <c>AllowsTransparency</c>, so <see cref="Window.Opacity"/> now
    /// genuinely blends the whole window (title bar + content) with the desktop. WPF renders
    /// layered windows in software, so the expensive gaussian ambient blur is dropped while translucent.
    /// </summary>
    private void UpdatePerformanceMode(double opacity)
    {
        var translucent = opacity < 0.999;
        if (translucent)
        {
            if (NpBackground.Effect is not null)
            {
                _npBlurEffect = NpBackground.Effect;
                NpBackground.Effect = null;
            }
        }
        else if (_npBlurEffect is not null)
        {
            NpBackground.Effect = _npBlurEffect;
            _npBlurEffect = null;
        }
    }

    private void EnsureTransparentWindowBackground()
    {
        // The visible surface lives on WindowFrame. The window must be transparent all the way down
        // to the composition target, otherwise Wpf.Ui's opaque window colour hides the desktop behind
        // the whole window and Window.Opacity never reveals anything.
        Background = Brushes.Transparent;
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget is not null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    private void UpdateFrameClip()
    {
        var radius = WindowState == WindowState.Maximized
            || string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_displayMode, "mode3d", StringComparison.OrdinalIgnoreCase)
            ? 0 : FrameCornerRadius;
        WindowFrame.Clip = new RectangleGeometry(
            new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight), radius, radius);
    }

    /* ===== FluentWindow overrides: keep WindowStyle=None so AllowsTransparency stays valid ===== */

    protected override void OnExtendsContentIntoTitleBarChanged(bool oldValue, bool newValue)
    {
        // FluentWindow normally forces WindowStyle=SingleBorderWindow here, which is illegal
        // together with AllowsTransparency. The window is already borderless, so nothing to do.
    }

    protected override void OnBackdropTypeChanged(Wpf.Ui.Controls.WindowBackdropType oldValue, Wpf.Ui.Controls.WindowBackdropType newValue)
    {
        // DWM system backdrops require a non-layered window, so they are skipped entirely.
    }

    protected override void SetWindowChrome()
    {
        // WindowChrome is incompatible with AllowsTransparency.
    }

    private void SetClickThrough(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLong(handle, GwlExStyle);
            var next = on ? (style | WsExTransparent) : (style & ~WsExTransparent);
            if (next != style) SetWindowLong(handle, GwlExStyle, next);
        }
        catch { /* ignore */ }
    }

    private static ImageSource LoadAppIcon()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return new DrawingImage(new GeometryDrawing());
        }
    }

    /* ===================== page switching ===================== */

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlays();
        _page = "playlist";
        PlaylistSearchBox.Text = "";
        Stage.Visibility = Visibility.Visible;
        ApplyPage();
        SyncTitleBarBackdrop();
    }

    private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e) => RenderQueue();

    private void PlaylistClose_Click(object sender, RoutedEventArgs e)
    {
        _page = null;
        ApplyPage();
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        // In visualizer mode the same title-bar button toggles the lyrics overlay that
        // sits on top of the visualization instead of navigating to the lyrics page.
        if (IsVisualizerMode)
        {
            _vizLyrics = !_vizLyrics;
            ApplyVisualizerLyrics();
            SaveSettings();
            return;
        }

        var open = _page != "lyrics";
        if (open) CloseOverlays();
        _page = open ? "lyrics" : null;
        Stage.Visibility = Visibility.Visible;
        ApplyPage();
        SyncTitleBarBackdrop();
    }

    /// <summary>Shows / hides the lyrics overlay that renders on top of the visualizer.</summary>
    private void ApplyVisualizerLyrics()
    {
        var show = IsVisualizerMode && _vizLyrics;
        VisualizerLyricsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateVisualizerInfoOverlay();
        if (!show) return;
        if (_lyricLines.Count == 0) RenderLyrics();
        else UpdateLyricHighlight(force: true);
    }

    /// <summary>Keeps the bottom song-info overlay hidden while the lyrics overlay is shown.</summary>
    private void UpdateVisualizerInfoOverlay()
    {
        var lyricsUp = VisualizerLyricsOverlay.Visibility == Visibility.Visible;
        VisualizerInfoOverlay.Visibility = IsVisualizerMode && _visualizerShowInfo && !lyricsUp
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyPage()
    {
        var album = _page is null;
        AlbumView.Visibility = album ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPage.Visibility = _page == "playlist" ? Visibility.Visible : Visibility.Collapsed;
        LyricsPage.Visibility = _page == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPage.Visibility = _page is "library" or "online" ? Visibility.Visible : Visibility.Collapsed;
        // Transparent pages must not reveal the visualizer that sits behind them.
        VisualizerView.Visibility = album && IsVisualizerMode ? Visibility.Visible : Visibility.Collapsed;
        if (_page == "lyrics") RenderLyrics();
        else if (_page == "playlist") RenderQueue();
        else if (_page is "library" or "online") RenderLibrary();
    }

    /* ===================== load / save ===================== */

    private async Task LoadAsync()
    {
        // Initialize DB lazily on Loaded — avoids XAML parse failure if the DB is
        // temporarily locked by orphan tray processes. Retry inside MusicDatabase handles
        // short locks; if it still fails we show a toast instead of crashing the window.
        try { _database = await Task.Run(() => new MusicDatabase(Path.Combine(_dataDirectory, "xstar.db"))); }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "XstarStartup.log"), $"[{DateTime.Now:O}] DB INIT FAIL\n{ex}\n"); } catch { }
            MessageBox.Show($"数据库初始化失败：{ex.Message}\n\n{ex}", "数据库错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (File.Exists(_settingsPath))
            {
                var state = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(_settingsPath));
                if (state?.Folders is not null) _folders.AddRange(state.Folders.Where(Directory.Exists));
                if (state is not null && state.LastPath is not null) _restoredPath = state.LastPath;
                _restoredQueue = state?.QueuePaths ?? new List<string>();
                _restoredPositionMs = state?.PositionMs ?? 0;
                _playbackMode = state?.PlaybackMode ?? PlaybackMode.Sequential;
                _lyricsFontSize = state?.LyricsFontSize is > 0 ? state.LyricsFontSize : 14;
                _dimLyrics = state?.DimLyrics ?? true;
                _cdLyrics = state?.CdLyrics ?? false;
                _lyricFontFamily = state?.LyricFontFamily ?? "";
                _lyricActiveColor = ParseColor(state?.LyricActiveColor);
                _lyricInactiveColor = ParseColor(state?.LyricInactiveColor);
                _discRotation = state?.DiscRotation ?? true;
                _displayMode = state?.DisplayMode ?? "normal";
                _discSize = state?.DiscSize is > 0 ? state.DiscSize : 1.2;
                _discSpeed = state?.DiscSpeed is > 0 ? state.DiscSpeed : 26;
                _cdZoom = state?.CdZoom is > 0 ? Math.Clamp(state.CdZoom, CdZoomMin, CdZoomMax) : 1.0;
                _windowOpacity = state?.WindowOpacity is >= 0 and <= 1 ? state.WindowOpacity : 1.0;
                _hideTitleBrand = state?.HideTitleBrand ?? false;
                _reduceMotion = state?.ReduceMotion ?? false;
                _themeName = state?.Theme ?? "system";
                _themePreset = state?.ThemePreset ?? "deepspace";
                _accentSource = state?.AccentSource ?? "cover";
                _jamendoClientId = state?.JamendoClientId ?? "";
                _downloadFolder = state?.DownloadFolder ?? "";
                _onlineProvider = state?.OnlineProvider ?? OnlineMusicService.AllProviders;
                _gdSource = state?.GdSource ?? "netease";
                _gdBitrate = state?.GdBitrate ?? 320;
                _audiusAppName = state?.AudiusAppName ?? "XstarMusic";
                ConfigureOnlineProviders();
                _eqEnabled = state?.EqEnabled ?? false;
                var gains = state?.EqGains;
                if (gains is not null && gains.Count == _eqGains.Length)
                    for (var i = 0; i < _eqGains.Length; i++) _eqGains[i] = gains[i];
                _visualizerStyle = state?.VisualizerStyle ?? "stardust";
                _visualizerShowInfo = state?.VisualizerShowInfo ?? true;
                _vizLyrics = state?.VisualizerLyrics ?? false;
                _threeDQuality = state?.ThreeDQuality ?? "balanced";
                _threeDShelfMode = state?.ThreeDShelfMode ?? "side";
                _threeDShelfPresence = state?.ThreeDShelfPresence ?? "always";
                _threeDCameraMode = state?.ThreeDCameraMode ?? "dynamic";
                _threeDLibraryKind = state?.ThreeDLibraryKind ?? "albums";
                _threeDCoverPreset = state?.ThreeDCoverPreset ?? 0;
                _threeDBloom = state?.ThreeDBloom ?? true;
                _threeDLyrics = state?.ThreeDLyrics ?? true;
                _threeDShelfSize = state?.ThreeDShelfSize is > 0.2 and < 3 ? state.ThreeDShelfSize : 1.0;
                _threeDShelfOffsetX = state?.ThreeDShelfOffsetX ?? 0;
                _threeDShelfOffsetY = state?.ThreeDShelfOffsetY ?? 0;
                _threeDShelfAngleY = state?.ThreeDShelfAngleY ?? 0;
                _threeDShelfOpacity = state?.ThreeDShelfOpacity is > 0 and <= 1 ? state.ThreeDShelfOpacity : 1.0;
                _threeDBackgroundColor = state?.ThreeDBackgroundColor ?? "#05060A";
                _threeDBackgroundImage = state?.ThreeDBackgroundImage ?? "";
                _threeDParticleSize = state?.ThreeDParticleSize is > 0.1 and < 4 ? state.ThreeDParticleSize : 1.0;
                _threeDGlow = state?.ThreeDGlow is >= 0 and <= 4 ? state.ThreeDGlow : 1.0;
                _threeDParticleCount = state?.ThreeDParticleCount is > 0.2 and < 2 ? state.ThreeDParticleCount : 1.0;
                _threeDParticleColorMode = state?.ThreeDParticleColorMode is >= 0 and <= 2 ? state.ThreeDParticleColorMode : 1;
                _threeDParticleColor = state?.ThreeDParticleColor ?? "";
            }
            ThemeManager.ApplyPreset(ThemeManager.GetPreset(_themePreset));
            ThemeManager.SetMode(ParseTheme(_themeName));
            ApplyWindowOpacity(_windowOpacity);
            ApplyTitleBrand();
            EnsureTransparentWindowBackground();
            ModeGlyphText.Text = ModeGlyph(_playbackMode);
            _player.SetMode(_playbackMode);
            UpdateOnlineButton();
            if (_folders.Count > 0) await ScanAsync();
            else RenderQueue();
            UpdateDiscRotation();
            SyncVisualizerTheme();
            _visualizerEngine.SetStyle(_visualizerStyle);
            ApplyDisplayMode();
            ApplyEqToEngine();
        }
        catch { /* 首次启动或损坏配置不阻断应用 */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AppState
            {
                Folders = _folders,
                LastPath = _currentIndex >= 0 && _currentIndex < _songs.Count ? _songs[_currentIndex].Song.Path : null,
                QueuePaths = _songs.Select(row => row.Song.Path).ToList(),
                PositionMs = _player.Snapshot.Position.TotalMilliseconds > 0 ? (long)_player.Snapshot.Position.TotalMilliseconds : 0,
                PlaybackMode = _playbackMode,
                LyricsFontSize = _lyricsFontSize,
                DimLyrics = _dimLyrics,
                CdLyrics = _cdLyrics,
                LyricFontFamily = _lyricFontFamily,
                LyricActiveColor = _lyricActiveColor is { } ac ? $"#{ac.A:X2}{ac.R:X2}{ac.G:X2}{ac.B:X2}" : null,
                LyricInactiveColor = _lyricInactiveColor is { } ic ? $"#{ic.A:X2}{ic.R:X2}{ic.G:X2}{ic.B:X2}" : null,
                DiscRotation = _discRotation,
                DisplayMode = _displayMode,
                DiscSize = _discSize,
                DiscSpeed = _discSpeed,
                CdZoom = _cdZoom,
                WindowOpacity = _windowOpacity,
                HideTitleBrand = _hideTitleBrand,
                ReduceMotion = _reduceMotion,
                Theme = _themeName,
                ThemePreset = _themePreset,
                AccentSource = _accentSource,
                JamendoClientId = _jamendoClientId,
                DownloadFolder = _downloadFolder,
                OnlineProvider = _onlineProvider,
                GdSource = _gdSource,
                GdBitrate = _gdBitrate,
                AudiusAppName = _audiusAppName,
                EqEnabled = _eqEnabled,
                EqGains = _eqGains.ToList(),
                VisualizerStyle = _visualizerStyle,
                VisualizerShowInfo = _visualizerShowInfo,
                VisualizerLyrics = _vizLyrics,
                ThreeDQuality = _threeDQuality,
                ThreeDShelfMode = _threeDShelfMode,
                ThreeDShelfPresence = _threeDShelfPresence,
                ThreeDCameraMode = _threeDCameraMode,
                ThreeDLibraryKind = _threeDLibraryKind,
                ThreeDCoverPreset = _threeDCoverPreset,
                ThreeDBloom = _threeDBloom,
                ThreeDLyrics = _threeDLyrics,
                ThreeDShelfSize = _threeDShelfSize,
                ThreeDShelfOffsetX = _threeDShelfOffsetX,
                ThreeDShelfOffsetY = _threeDShelfOffsetY,
                ThreeDShelfAngleY = _threeDShelfAngleY,
                ThreeDShelfOpacity = _threeDShelfOpacity,
                ThreeDBackgroundColor = _threeDBackgroundColor,
                ThreeDBackgroundImage = _threeDBackgroundImage,
                ThreeDParticleSize = _threeDParticleSize,
                ThreeDGlow = _threeDGlow,
                ThreeDParticleCount = _threeDParticleCount,
                ThreeDParticleColorMode = _threeDParticleColorMode,
                ThreeDParticleColor = _threeDParticleColor
            }));
        }
        catch { }
    }

    /* ===================== scanning ===================== */

    private async Task ScanAsync()
    {
        // Cancel any previous scan (debounce + running)
        _scanDebounce?.Cancel();
        var prev = _scanCts;
        prev?.Cancel();
        // Allow a brief moment for the previous Task to observe cancellation
        if (_scanning) await Task.Delay(50);

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _scanning = true;
        OnScanStatusChanged(true);

        try
        {
            if (_folders.Count == 0)
            {
                _songs.Clear();
                RenderQueue();
                RenderLibrary();
                return;
            }

            var progress = new Progress<(int Done, int Total, string Status)>(p =>
            {
                if (_settingsScanProgress == null) return;
                _settingsScanProgress.Maximum = Math.Max(p.Total, 1);
                _settingsScanProgress.Value = Math.Min(p.Done, p.Total);
                _settingsScanStatus.Text = p.Status;
                _settingsScanCount.Text = p.Total > 0 ? $"{p.Done}/{p.Total}" : "";
            });

            // Heavy work off UI thread
            var snapshot = await Task.Run(async () =>
            {
                var scanned = await _scanner.ScanAsync(_folders, progress, cts.Token);
                cts.Token.ThrowIfCancellationRequested();

                _database.UpsertSongs(scanned);
                _database.ReplaceScanFiles(scanned.Select(song => song.Path));
                _database.RemoveMissingFiles();

                var favorites = _database.ReadFavoriteIds();
                var histories = _database.ReadHistory();
                var playlists = _database.ReadPlaylists();
                var playlistSongs = new Dictionary<int, HashSet<string>>();
                foreach (var p in playlists) playlistSongs[p.Id] = _database.ReadPlaylistSongIds(p.Id);

                var dbSongs = _database.ReadSongs();
                var rows = new List<SongRow>(dbSongs.Count);
                var idx = 1;
                foreach (var song in dbSongs)
                    rows.Add(new SongRow(song, idx++) { IsFavorite = favorites.Contains(song.Id) });
                return (rows, favorites, histories, playlists, playlistSongs);
            }, cts.Token);

            // Apply on UI thread
            if (_settingsScanStatus != null) _settingsScanStatus.Text = "正在刷新界面…";
            _favoriteIds = snapshot.favorites;
            _historyTicks = snapshot.histories;
            _playlists = snapshot.playlists;
            _playlistSongs.Clear();
            foreach (var kv in snapshot.playlistSongs) _playlistSongs[kv.Key] = kv.Value;
            _songs.Clear();
            foreach (var row in snapshot.rows) _songs.Add(row);
            RenderQueue();
            RenderLibrary();
            ConfigureWatchers();
            if (!_restoredOnce) { _restoredOnce = true; RestorePlayback(); }
            if (_settingsScanStatus != null) _settingsScanStatus.Text = $"完成，共 {snapshot.rows.Count} 首";
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            if (_settingsScanStatus != null) _settingsScanStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            if (_settingsScanStatus != null) _settingsScanStatus.Text = "扫描失败";
            MessageBox.Show($"扫描失败：{ex.Message}\n\n{ex}", "扫描失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // Reference check: if a newer scan replaced us, don't clear its state.
            if (_scanCts == cts) { _scanning = false; _scanCts = null; OnScanStatusChanged(false); }
            cts.Dispose();
        }
    }

    private void CancelScan()
    {
        _scanCts?.Cancel();
        _scanDebounce?.Cancel();
    }

    private void ScanCancel_Click(object sender, RoutedEventArgs e) => CancelScan();

    private StackPanel? _settingsScanArea;
    private ProgressBar? _settingsScanProgress;
    private TextBlock? _settingsScanStatus;
    private TextBlock? _settingsScanCount;

    private void OnScanStatusChanged(bool scanning)
    {
        if (_settingsScanArea == null) return;
        _settingsScanArea.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning)
        {
            _settingsScanProgress!.Value = 0;
            _settingsScanProgress.Maximum = 1;
            _settingsScanStatus!.Text = "正在扫描…";
            _settingsScanCount!.Text = "";
        }
    }

    private void RefreshLibraryMeta()
    {
        _favoriteIds = _database.ReadFavoriteIds();
        _historyTicks = _database.ReadHistory();
        _playlists = _database.ReadPlaylists();
        _playlistSongs.Clear();
        foreach (var playlist in _playlists) _playlistSongs[playlist.Id] = _database.ReadPlaylistSongIds(playlist.Id);
    }

    private void RestorePlayback()
    {
        if (_songs.Count == 0) return;
        var rows = _songs.ToList();
        var restoredRows = _restoredQueue
            .Select(path => rows.FirstOrDefault(row => row.Song.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .Where(row => row is not null)
            .Cast<SongRow>()
            .ToList();
        foreach (var row in rows.Where(row => !restoredRows.Contains(row))) restoredRows.Add(row);
        if (restoredRows.Count == rows.Count)
        {
            _songs.Clear();
            foreach (var row in restoredRows) _songs.Add(row);
        }
        var index = _restoredPath is null ? -1 : _songs.ToList().FindIndex(row => row.Song.Path.Equals(_restoredPath, StringComparison.OrdinalIgnoreCase));
        SelectSong(index >= 0 ? index : 0, false);
    }

    /* ===================== list rendering ===================== */

    private void RenderQueue()
    {
        IEnumerable<SongRow> rows = _songs;
        var keyword = PlaylistSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
            rows = rows.Where(x => $"{x.Title}{x.Artist}{x.Song.Album}".Contains(keyword, StringComparison.OrdinalIgnoreCase));
        SongList.ItemsSource = rows.ToList();
    }

    private void RenderLibrary()
    {
        LibraryList.ItemTemplate = (DataTemplate)FindResource(_onlineMode ? "OnlineSongTemplate" : "PlaylistSongTemplate");
        if (_onlineMode)
        {
            LibraryList.ItemsSource = _onlineResults.ToList();
            SetLibraryListVisible(true);
            return;
        }

        IEnumerable<SongRow> rows = _songs;
        var keyword = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(keyword)) rows = rows.Where(x => $"{x.Title}{x.Artist}{x.Song.Album}".Contains(keyword, StringComparison.OrdinalIgnoreCase));

        if (_view == "playlists")
        {
            var cards = _playlists
                .Select(p => new LibraryItem(p.Name, $"{(_playlistSongs.TryGetValue(p.Id, out var set) ? set.Count : 0)} 首", null, 0))
                .ToList();
            LibraryGrid.ItemsSource = cards;
            SetLibraryListVisible(false);
            return;
        }

        if (_view is "albums" or "artists")
        {
            var cards = _view == "albums"
                ? rows.GroupBy(x => x.Song.Album).Select(g => new LibraryItem(g.Key, g.First().Song.Artist, g.First().Song.ArtworkPath, g.Count())).ToList()
                : rows.GroupBy(x => x.Song.Artist).Select(g => new LibraryItem(g.Key, $"{g.Count()} 首", g.First().Song.ArtworkPath, g.Count())).ToList();
            LibraryGrid.ItemsSource = cards;
            SetLibraryListVisible(false);
            return;
        }

        if (_view == "favorites") rows = rows.Where(x => x.IsFavorite);
        else if (_view == "recent") rows = rows.Where(x => _historyTicks.ContainsKey(x.Song.Id)).OrderByDescending(x => _historyTicks[x.Song.Id]);
        else if (_view.StartsWith("playlist:", StringComparison.Ordinal) && int.TryParse(_view[9..], out var playlistId))
        {
            if (_playlistSongs.TryGetValue(playlistId, out var ids)) rows = rows.Where(x => ids.Contains(x.Song.Id));
            else { _view = "songs"; }
        }
        LibraryList.ItemsSource = rows.ToList();
        SetLibraryListVisible(true);
    }

    /// <summary>Toggles between the list view (with column header) and the album/artist card grid.</summary>
    private void SetLibraryListVisible(bool list)
    {
        LibraryList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        LibraryGridScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        LibraryColumns.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        LibraryDivider.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
    }

    /* ===================== selection / playback ===================== */

    private void SelectSong(int index, bool play)
    {
        if (index < 0 || index >= _songs.Count) return;
        _currentIndex = index;
        _currentOnline = null;
        var song = _songs[index].Song;
        if (play)
        {
            var playedAt = DateTime.UtcNow.Ticks;
            _database.AddHistory(song.Id, playedAt);
            _historyTicks[song.Id] = playedAt;
        }

        UpdateNowPlayingUi(song);

        if (File.Exists(song.Path))
        {
            try
            {
                _player.Load(song);
                if (play) _player.Play();
            }
            catch { }
        }

        RenderLyrics();
        PlayGlyphText.Text = play ? "\uE769" : "\uE768";
        AnimateNowPlaying();
        _timer.Start();
        UpdateDiscRotation();
    }

    private void UpdateNowPlayingUi(Song song)
    {
        TitleText.Text = song.Title;
        SubText.Text = string.IsNullOrWhiteSpace(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}";
        CdTitleText.Text = song.Title;
        CdSubText.Text = song.Artist;
        VizLyricsTitle.Text = song.Title;
        VizLyricsSub.Text = song.Artist;
        LyricsHead.Text = song.Title;
        LyricsSub.Text = song.Artist;
        var artwork = ArtworkLoader.LoadForSong(song);
        QualityText.Text = song.Quality;
        ApplyQualityVisual(song.Quality);
        var spec = BuildMetaText(song);
        SpecChip.Visibility = string.IsNullOrWhiteSpace(spec) ? Visibility.Collapsed : Visibility.Visible;
        SpecText.Text = spec ?? "";
        VizSpecChip.Visibility = SpecChip.Visibility;
        VizSpecText.Text = spec ?? "";

        var accent = _accentSource == "cover"
            ? (CoverColorExtractor.Extract(artwork) ?? ThemeManager.CurrentPreset.Accent)
            : ThemeManager.CurrentPreset.Accent;
        ThemeManager.ApplyAccent(accent);
        UpdateNowPlayingGradient();
        _discArtwork = artwork;
        UpdateDiscLabel(artwork, accent);

        VisualizerTitleText.Text = song.Title;
        VisualizerSubText.Text = string.IsNullOrWhiteSpace(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}";
        _threeDHost.PushArtwork();
    }

    private void UpdateNowPlayingGradient()
    {
        var accent = ThemeManager.AccentColor;
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.42),
            Center = new Point(0.5, 0.42),
            RadiusX = 0.55,
            RadiusY = 0.6
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, accent.R, accent.G, accent.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
        NpBackground.Background = brush;

        var glow = new RadialGradientBrush();
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, accent.R, accent.G, accent.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
        PlayGlow.Fill = glow;

        // Keep the visualizer accent/backdrop in sync with cover- or preset-derived colours.
        SyncVisualizerTheme();
    }

    private static ImageSource BuildGrooveImage(double diameter)
    {
        var c = diameter / 2.0;
        var outer = c - 5;
        const double labelRadius = 74;

        var group = new DrawingGroup
        {
            ClipGeometry = new EllipseGeometry(new Point(c, c), outer, outer)
        };
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Colors.Transparent), null, new RectangleGeometry(new Rect(0, 0, diameter, diameter))));

        var rnd = new Random(20240517);
        for (var r = labelRadius; r <= outer; r += 1.35)
        {
            var grooveBrush = new SolidColorBrush(Color.FromArgb((byte)(0x0C + rnd.Next(0, 0x14)), 255, 255, 255));
            grooveBrush.Freeze();
            var groovePen = new Pen(grooveBrush, 0.55 + rnd.NextDouble() * 0.45);
            groovePen.Freeze();
            group.Children.Add(new GeometryDrawing(null, groovePen, new EllipseGeometry(new Point(c, c), r, r)));

            if (rnd.NextDouble() < 0.085)
            {
                var landBrush = new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0));
                landBrush.Freeze();
                var landPen = new Pen(landBrush, 1.8 + rnd.NextDouble() * 1.2);
                landPen.Freeze();
                group.Children.Add(new GeometryDrawing(null, landPen, new EllipseGeometry(new Point(c, c), r + 0.7, r + 0.7)));
            }
        }

        group.Freeze();
        return new DrawingImage(group);
    }

    private static ImageSource BuildLabelTextureImage(double diameter)
    {
        var c = diameter / 2.0;
        var group = new DrawingGroup
        {
            ClipGeometry = new EllipseGeometry(new Point(c, c), c, c)
        };
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Colors.Transparent), null, new RectangleGeometry(new Rect(0, 0, diameter, diameter))));

        var rnd = new Random(77123);
        for (var r = 9.0; r <= c; r += 1.05)
        {
            var ringBrush = new SolidColorBrush(Color.FromArgb((byte)(0x06 + rnd.Next(0, 0x0D)), 255, 255, 255));
            ringBrush.Freeze();
            var ringPen = new Pen(ringBrush, 0.5 + rnd.NextDouble() * 0.4);
            ringPen.Freeze();
            group.Children.Add(new GeometryDrawing(null, ringPen, new EllipseGeometry(new Point(c, c), r, r)));
        }

        var vignette = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0));
        vignette.Freeze();
        var vignettePen = new Pen(vignette, 2.0);
        vignettePen.Freeze();
        group.Children.Add(new GeometryDrawing(null, vignettePen, new EllipseGeometry(new Point(c, c), c - 1.6, c - 1.6)));

        group.Freeze();
        return new DrawingImage(group);
    }

    private void UpdateDiscLabel(ImageSource? artwork, Color accent)
    {
        if (artwork is not null)
        {
            DiscArtworkBrush.ImageSource = artwork;
            DiscArtwork.Visibility = Visibility.Visible;
            DiscScrim.Visibility = Visibility.Visible;
            DiscLabel.Visibility = Visibility.Collapsed;
            DiscLabelTexture.Visibility = Visibility.Collapsed;
            DiscLabelRing.Visibility = Visibility.Collapsed;
            return;
        }

        DiscArtwork.Visibility = Visibility.Collapsed;
        DiscScrim.Visibility = Visibility.Collapsed;
        DiscArtworkBrush.ImageSource = null;
        DiscLabel.Visibility = Visibility.Visible;
        DiscLabelTexture.Visibility = Visibility.Visible;
        DiscLabelRing.Visibility = Visibility.Visible;

        DiscLabelBrush.ImageSource = null;
        var light = Color.FromRgb(
            (byte)(accent.R * 0.78),
            (byte)(accent.G * 0.78),
            (byte)(accent.B * 0.78));
        var dark = Color.FromRgb(
            (byte)(accent.R * 0.46),
            (byte)(accent.G * 0.46),
            (byte)(accent.B * 0.46));
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.32),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.85,
            RadiusY = 0.85
        };
        brush.GradientStops.Add(new GradientStop(light, 0));
        brush.GradientStops.Add(new GradientStop(dark, 1));
        DiscLabel.Fill = brush;
    }

    private void UpdateDiscRotation()
    {
        if (NowPlayingDisc is null) return;
        var cd = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase);
        var show = _currentIndex >= 0 || cd;
        NowPlayingDisc.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        var scale = _discSize;
        var side = Math.Min(Stage.ActualWidth, Stage.ActualHeight);
        if (cd)
        {
            // Fill the fixed CD window, then apply the user's wheel/pinch zoom on top.
            var fit = side > 40 ? side / 340.0 : _discSize;
            scale = fit * _cdZoom;
        }
        else if (side > 40)
        {
            // Shrink the disc on narrow/short windows so it never overflows the stage.
            scale = Math.Min(_discSize, Math.Max(0.55, (side - 24) / 340.0));
        }

        DiscScale.ScaleX = scale;
        DiscScale.ScaleY = scale;

        if (DiscClickZones is not null)
        {
            DiscClickZones.Width = 340 * scale;
            DiscClickZones.Height = 340 * scale;
            DiscClickZones.Visibility = show && cd
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!show) return;

        if (_discClock is null || Math.Abs(_discClockSpeed - _discSpeed) > 0.01)
        {
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(_discSpeed)) { RepeatBehavior = RepeatBehavior.Forever };
            var clock = anim.CreateClock();
            DiscRotate.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
            _discClock = clock.Controller;
            _discClockSpeed = _discSpeed;
        }

        if (_discRotation && !_reduceMotion && _player.Snapshot.IsPlaying) _discClock.Resume();
        else _discClock.Pause();
    }

    private void AnimateNowPlaying()
    {
        if (_reduceMotion || !SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        NowPlayingInfo.Opacity = 0;
        NowPlayingInfo.RenderTransform = new TranslateTransform(0, 10);
        NowPlayingInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        ((TranslateTransform)NowPlayingInfo.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    /* ===================== display mode ===================== */

    /// <summary>Opaque window surface used behind the visualizer (theme-tinted, refreshed by <see cref="SyncVisualizerTheme"/>).</summary>
    private Brush _vizSurfaceBrush = Brushes.Black;

    /// <summary>Light caption foreground forced while the (always-dark) visualizer is shown.</summary>
    private static readonly Brush VizTitleBarForeground = new SolidColorBrush(Color.FromArgb(235, 233, 236, 242));

    /// <summary>Inactive lyric colour used over the (always-dark) visualizer backdrop.</summary>
    private static readonly Brush VizLyricInactiveBrush = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));

    private void ApplyVisualizerTitleBarChrome(bool viz)
    {
        if (viz)
        {
            // The visualizer backdrop is always dark — keep caption text/buttons light
            // even while a light theme preset is active.
            TitleBarControl.Foreground = VizTitleBarForeground;
            TitleBarControl.ButtonsForeground = VizTitleBarForeground;
            LibraryButton.Foreground = VizTitleBarForeground;
            SettingsButton.Foreground = VizTitleBarForeground;
            ViewModeButton.Foreground = VizTitleBarForeground;
        }
        else
        {
            TitleBarControl.ClearValue(Control.ForegroundProperty);
            TitleBarControl.ClearValue(Wpf.Ui.Controls.TitleBar.ButtonsForegroundProperty);
            LibraryButton.ClearValue(Control.ForegroundProperty);
            SettingsButton.ClearValue(Control.ForegroundProperty);
            ViewModeButton.ClearValue(Control.ForegroundProperty);
        }
    }

    /// <summary>
    /// The visualizer backdrop is always dark, so the transport bar keeps a light foreground
    /// there even while a light theme preset is active.
    /// </summary>
    private void ApplyVisualizerPlaybackChrome(bool viz)
    {
        var transport = new[] { ModeButton, PreviousButton, PlayButton, NextButton, VolumeButton, VizStyleButton };
        VizStyleHost.Visibility = viz ? Visibility.Visible : Visibility.Collapsed;
        if (viz)
        {
            foreach (var button in transport) button.Foreground = VizTitleBarForeground;
            // Blend the play/pause control into the visualization instead of its accent disc.
            PlayButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
            CurrentTime.Foreground = VizTitleBarForeground;
            TotalTime.Foreground = VizTitleBarForeground;
        }
        else
        {
            foreach (var button in transport) button.ClearValue(Control.ForegroundProperty);
            PlayButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            CurrentTime.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            TotalTime.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            VizStylePopup.IsOpen = false;
        }
    }

    /// <summary>Reveals the transport bar, fading it in and restarting the auto-hide timer.</summary>
    private void ShowVisualizerControls(bool restartTimer = true)
    {
        if (!IsVisualizerMode) return;
        if (!_vizControlsVisible)
        {
            _vizControlsVisible = true;
            PlaybackBar.IsHitTestVisible = true;
            if (_reduceMotion)
            {
                PlaybackBar.BeginAnimation(OpacityProperty, null);
                PlaybackBar.Opacity = 1;
            }
            else
            {
                PlaybackBar.BeginAnimation(OpacityProperty, new DoubleAnimation(PlaybackBar.Opacity, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }
        if (restartTimer) { _vizControlsTimer.Stop(); _vizControlsTimer.Start(); }
    }

    /// <summary>Fades the transport bar out once the pointer has been idle. Kept while hovered or while a popup is open.</summary>
    private void HideVisualizerControls()
    {
        _vizControlsTimer.Stop();
        if (!IsVisualizerMode) return;
        if (PlaybackBar.IsMouseOver || VolumePopup.IsOpen || VizStylePopup.IsOpen)
        {
            _vizControlsTimer.Start();
            return;
        }
        if (!_vizControlsVisible) return;
        _vizControlsVisible = false;
        PlaybackBar.IsHitTestVisible = false;
        if (_reduceMotion)
        {
            PlaybackBar.BeginAnimation(OpacityProperty, null);
            PlaybackBar.Opacity = 0;
        }
        else
        {
            PlaybackBar.BeginAnimation(OpacityProperty, new DoubleAnimation(PlaybackBar.Opacity, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
        }
    }

    /// <summary>Restores the transport bar's always-visible state used outside visualizer mode.</summary>
    private void ResetVisualizerControls()
    {
        _vizControlsTimer.Stop();
        _vizControlsVisible = false;
        PlaybackBar.BeginAnimation(OpacityProperty, null);
        PlaybackBar.Opacity = 1;
        PlaybackBar.IsHitTestVisible = true;
    }

    private void ApplyDisplayMode()
    {
        var cd = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase);
        var viz = string.Equals(_displayMode, "visualizer", StringComparison.OrdinalIgnoreCase);
        var mode3d = string.Equals(_displayMode, "mode3d", StringComparison.OrdinalIgnoreCase);
        var immersive = cd || viz || mode3d;

        // Album / playlist / lyrics visibility
        AlbumView.Visibility = (viz || mode3d) ? Visibility.Collapsed : Visibility.Visible;
        PlaylistPage.Visibility = Visibility.Collapsed;  // always hidden in viz mode
        LyricsPage.Visibility = Visibility.Collapsed;

        // Visualizer view visibility
        VisualizerView.Visibility = viz ? Visibility.Visible : Visibility.Collapsed;
        ThreeDView.Visibility = mode3d ? Visibility.Visible : Visibility.Collapsed;

        TitleBarControl.Opacity = (cd || mode3d) ? 0 : 1;
        TitleBarControl.IsHitTestVisible = !(cd || mode3d);
        TitleBarControl.Height = (cd || mode3d) ? 0 : 32;
        ApplyVisualizerTitleBarChrome(viz);
        ApplyVisualizerPlaybackChrome(viz);
        NpBackground.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        // CD mode hides the transport entirely; visualizer mode keeps it (auto-hiding) so
        // playback can be controlled without leaving the immersive view. 3D mode draws its
        // own transport inside the page.
        PlaybackBar.Visibility = (cd || mode3d) ? Visibility.Collapsed : Visibility.Visible;
        InfoChips.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        // Only CD mode hides the whole title bar (the remaining title-bar buttons stay in both
        // normal and visualizer mode).
        LibraryButton.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingDisc.Margin = cd ? new Thickness(0) : new Thickness(0, 0, 0, 72);
        NowPlayingInfo.Margin = cd ? new Thickness(24) : new Thickness(24, 0, 24, 72);
        DiscBase.Effect = cd ? null : _discShadow;
        // Only CD mode handles touch pinch; elsewhere it would swallow normal touch input.
        Stage.IsManipulationEnabled = cd;

        if (cd)
        {
            // CD mode keeps a fully transparent window surface (desktop shows around the disc).
            WindowFrame.Background = Brushes.Transparent;
            WindowFrame.BorderBrush = null;
            WindowFrame.BorderThickness = new Thickness(0);
            WindowFrame.CornerRadius = new CornerRadius(0);
            EnsureTransparentWindowBackground();
        }
        else if (mode3d)
        {
            // 3D mode is fully immersive: an opaque themed-dark surface sits behind the
            // WebView2 (a layered window would otherwise skip transparent pixels).
            SyncVisualizerTheme();
            WindowFrame.Background = _vizSurfaceBrush;
            WindowFrame.BorderBrush = null;
            WindowFrame.BorderThickness = new Thickness(0);
            WindowFrame.CornerRadius = new CornerRadius(0);
            EnsureTransparentWindowBackground();
        }
        else if (viz)
        {
            // Visualizer mode uses an opaque, theme-tinted surface. It must not be transparent:
            // layered windows skip fully-transparent pixels during hit testing, which made the
            // title bar un-draggable (and the caption buttons hard to click).
            WindowFrame.Background = _vizSurfaceBrush;
            WindowFrame.BorderBrush = null;
            WindowFrame.BorderThickness = new Thickness(0);
            WindowFrame.CornerRadius = new CornerRadius(0);
            EnsureTransparentWindowBackground();
        }
        else
        {
            WindowFrame.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            WindowFrame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            WindowFrame.BorderThickness = new Thickness(1);
            WindowFrame.CornerRadius = new CornerRadius(FrameCornerRadius);
        }

        if (viz)
        {
            SyncVisualizerTheme();
            _page = null;
            ApplyPage();
            CloseOverlays();
            Stage.Visibility = Visibility.Visible;
            SyncTitleBarBackdrop();

            // Start visualizer
            if (!_visualizerEngine.IsRunning)
            {
                // Wait until VisualizerView is laid out
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (VisualizerView.ActualWidth > 10 && VisualizerView.ActualHeight > 10)
                    {
                        _visualizerEngine.SetBitmapSize(
                            (int)Math.Round(VisualizerView.ActualWidth),
                            (int)Math.Round(VisualizerView.ActualHeight));
                        VisualizerImage.Source = _visualizerEngine.Bitmap;
                        _visualizerEngine.Start(_reduceMotion);
                    }
                }), DispatcherPriority.Loaded);
            }
            VisualizerTitleText.Text = _currentIndex >= 0 ? _songs[_currentIndex].Title : "还没有歌曲";
            VisualizerSubText.Text = _currentIndex >= 0
                ? (string.IsNullOrWhiteSpace(_songs[_currentIndex].Song.Album)
                    ? _songs[_currentIndex].Artist
                    : $"{_songs[_currentIndex].Artist} · {_songs[_currentIndex].Song.Album}")
                : "";
            // Lift the info overlay above the auto-hiding transport bar.
            VisualizerInfoOverlay.Margin = new Thickness(0, 0, 0, 150);
            UpdateVisualizerInfoOverlay();
            ShowVisualizerControls();
        }
        else
        {
            // Stop visualizer
            _visualizerEngine.Stop();
            ResetVisualizerControls();
        }

        if (mode3d)
        {
            _page = null;
            ApplyPage();
            CloseOverlays();
            StartThreeDMode();
        }
        else
        {
            _threeDHost.Stop();
        }

        ApplyVisualizerLyrics();
        ApplyCdWindow(cd);
        ApplyCdChrome(cd);
        ApplyCdLyrics();
        UpdateFrameClip();
        UpdateDiscRotation();
    }

    /* ===================== 3D mode (WebView2 + Three.js) ===================== */

    private async void StartThreeDMode()
    {
        var ok = await _threeDHost.StartAsync();
        if (ok) return;
        // WebView2 runtime missing / init failed — record the reason and fall back to the normal shell.
        ThreeDHost.LogClientError("3D start failed: " + _threeDHost.LastError);
        _displayMode = "normal";
        ApplyDisplayMode();
        SaveSettings();
        Toast("3D 模式需要 WebView2 运行时，未能启动。");
    }

    private static string AccentHex()
    {
        var c = ThemeManager.AccentColor;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private (string Title, string Artist, string Album) CurrentTrackInfo()
    {
        if (_currentOnline is not null)
            return (_currentOnline.Title, _currentOnline.Artist, _currentOnline.Album);
        if (_currentIndex >= 0 && _currentIndex < _songs.Count)
        {
            var row = _songs[_currentIndex];
            return (row.Title, row.Artist, row.Song.Album);
        }
        return ("", "", "");
    }

    string IThreeDActions.ThreeDGetStateJson()
    {
        var (title, artist, album) = CurrentTrackInfo();
        var snapshot = _player.Snapshot;
        var message = new ThreeDStateMessage
        {
            Title = title,
            Artist = artist,
            Album = album,
            DurationMs = (long)snapshot.Duration.TotalMilliseconds,
            PositionMs = (long)snapshot.Position.TotalMilliseconds,
            Playing = snapshot.IsPlaying,
            Accent = AccentHex(),
            Volume = VolumeSlider.Value,
            Light = ThemeManager.IsLight
        };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetArtworkJson()
    {
        var artworkKey = _currentIndex >= 0 && _currentIndex < _songs.Count
            ? _songs[_currentIndex].Song.Path
            : _currentOnline?.Path;
        var message = new ThreeDArtworkMessage
        {
            Data = ArtworkBridge.Encode(_discArtwork as BitmapSource, artworkKey)
        };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetLibraryJson(string kind)
    {
        var cards = new List<ThreeDCard>();
        switch (kind)
        {
            case "playlists":
                cards.Add(new ThreeDCard
                {
                    Id = "favorites",
                    Title = "收藏",
                    Subtitle = "我喜欢的音乐",
                    Kind = "collection",
                    Count = _songs.Count(x => x.IsFavorite)
                });
                cards.AddRange(_playlists.Select(p => new ThreeDCard
                {
                    Id = "playlist:" + p.Id,
                    Title = p.Name,
                    Subtitle = (_playlistSongs.TryGetValue(p.Id, out var set) ? set.Count : 0) + " 首",
                    Kind = "collection",
                    Count = _playlistSongs.TryGetValue(p.Id, out var s) ? s.Count : 0
                }));
                break;
            case "recent":
                cards.AddRange(_songs
                    .Where(x => _historyTicks.ContainsKey(x.Song.Id))
                    .OrderByDescending(x => _historyTicks[x.Song.Id])
                    .Take(80)
                    .Select(x => new ThreeDCard
                    {
                        Id = "track:" + x.Song.Id,
                        Title = x.Title,
                        Subtitle = x.Artist,
                        Kind = "track"
                    }));
                break;
            default: // albums
                cards.AddRange(_songs
                    .GroupBy(x => x.Song.Album)
                    .OrderBy(g => g.Key, StringComparer.CurrentCulture)
                    .Select(g => new ThreeDCard
                    {
                        Id = "album:" + g.Key,
                        Title = g.Key,
                        Subtitle = g.First().Song.Artist,
                        Kind = "collection",
                        Count = g.Count()
                    }));
                break;
        }

        _threeDLibraryKind = kind;
        var message = new ThreeDLibraryMessage { Kind = kind, Cards = cards };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetDetailJson(string id)
    {
        var title = id;
        var subtitle = "";
        var rows = new List<SongRow>();

        if (id.StartsWith("album:", StringComparison.Ordinal))
        {
            var album = id[6..];
            title = album;
            rows = _songs.Where(x => x.Song.Album == album).OrderBy(x => x.Song.Title, StringComparer.CurrentCulture).ToList();
        }
        else if (id.StartsWith("playlist:", StringComparison.Ordinal) && int.TryParse(id[9..], out var playlistId))
        {
            var playlist = _playlists.FirstOrDefault(p => p.Id == playlistId);
            title = string.IsNullOrEmpty(playlist.Name) ? "播放列表" : playlist.Name;
            if (_playlistSongs.TryGetValue(playlistId, out var set))
                rows = _songs.Where(x => set.Contains(x.Song.Id)).ToList();
        }
        else if (id == "favorites")
        {
            title = "收藏";
            rows = _songs.Where(x => x.IsFavorite).ToList();
        }

        subtitle = rows.Count + " 首";
        var tracks = rows.Select((x, i) => new ThreeDTrack
        {
            Id = x.Song.Id,
            Title = x.Title,
            Artist = x.Artist,
            DurationMs = x.Song.DurationMs,
            Index = i
        }).ToList();

        var message = new ThreeDDetailMessage { Id = id, Title = title, Subtitle = subtitle, Tracks = tracks };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetCardArtworkJson(string key)
    {
        if (!_threeDCardArt.TryGetValue(key, out var data))
        {
            var song = ResolveCardSong(key);
            BitmapSource? bitmap = null;
            if (song is not null)
            {
                bitmap = !string.IsNullOrEmpty(song.ArtworkPath) && File.Exists(song.ArtworkPath)
                    ? ArtworkLoader.Load(song.ArtworkPath)
                    : ArtworkLoader.LoadForSong(song);
            }
            data = ArtworkBridge.EncodeBitmap(bitmap, 160);
            _threeDCardArt[key] = data;
        }

        var message = new ThreeDArtworkMessage { Key = key, Data = data };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetLyricsJson()
    {
        var lines = _lyricLines
            .Select(line => new ThreeDLyricLine { TimeMs = line.TimeMs, Text = line.Text })
            .ToList();
        var message = new ThreeDLyricsMessage { Lines = lines };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetLyricIndexJson()
    {
        var message = new ThreeDLyricIndexMessage { Index = _lastLyricIndex };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    string IThreeDActions.ThreeDGetSettingsJson()
    {
        var message = new ThreeDSettingsMessage
        {
            Quality = _threeDQuality,
            ShelfMode = _threeDShelfMode,
            ShelfPresence = _threeDShelfPresence,
            CameraMode = _threeDCameraMode,
            LibraryKind = _threeDLibraryKind,
            CoverPreset = _threeDCoverPreset,
            Bloom = _threeDBloom,
            Lyrics = _threeDLyrics,
            ReduceMotion = _reduceMotion,
            ShelfSize = _threeDShelfSize,
            ShelfOffsetX = _threeDShelfOffsetX,
            ShelfOffsetY = _threeDShelfOffsetY,
            ShelfAngleY = _threeDShelfAngleY,
            ShelfOpacity = _threeDShelfOpacity,
            BackgroundColor = _threeDBackgroundColor,
            BackgroundImage = EncodeBackgroundImage(),
            ParticleSize = _threeDParticleSize,
            Glow = _threeDGlow,
            ParticleCount = _threeDParticleCount,
            ParticleColorMode = _threeDParticleColorMode,
            ParticleColor = _threeDParticleColor
        };
        return JsonSerializer.Serialize(message, ThreeDJson.Options);
    }

    private string? EncodeBackgroundImage()
    {
        if (string.IsNullOrEmpty(_threeDBackgroundImage) || !File.Exists(_threeDBackgroundImage)) return null;
        if (_threeDBackgroundDataKey == _threeDBackgroundImage) return _threeDBackgroundData;
        _threeDBackgroundData = ArtworkBridge.EncodeBitmap(ArtworkLoader.Load(_threeDBackgroundImage), 1920);
        _threeDBackgroundDataKey = _threeDBackgroundImage;
        return _threeDBackgroundData;
    }

    void IThreeDActions.ThreeDPickBackgroundImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 3D 背景图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        _threeDBackgroundImage = dialog.FileName;
        _threeDBackgroundDataKey = "";
        SaveSettings();
        _threeDHost.PushSettings();
    }

    void IThreeDActions.ThreeDClearBackgroundImage()
    {
        _threeDBackgroundImage = "";
        _threeDBackgroundData = null;
        _threeDBackgroundDataKey = "";
        SaveSettings();
        _threeDHost.PushSettings();
    }

    private Song? ResolveCardSong(string cardId)
    {
        if (cardId.StartsWith("album:", StringComparison.Ordinal))
        {
            var album = cardId[6..];
            return _songs.FirstOrDefault(x => x.Song.Album == album)?.Song;
        }
        if (cardId.StartsWith("playlist:", StringComparison.Ordinal) && int.TryParse(cardId[9..], out var playlistId))
        {
            if (_playlistSongs.TryGetValue(playlistId, out var set))
                return _songs.FirstOrDefault(x => set.Contains(x.Song.Id))?.Song;
            return null;
        }
        if (cardId == "favorites")
            return _songs.FirstOrDefault(x => x.IsFavorite)?.Song;
        if (cardId.StartsWith("track:", StringComparison.Ordinal))
        {
            var songId = cardId[6..];
            return _songs.FirstOrDefault(x => x.Song.Id == songId)?.Song;
        }
        return null;
    }

    void IThreeDActions.ThreeDPlay()
    {
        if (!_player.Snapshot.IsPlaying) Play_Click(this, new RoutedEventArgs());
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDPause()
    {
        if (_player.Snapshot.IsPlaying) Play_Click(this, new RoutedEventArgs());
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDNext()
    {
        Next_Click(this, new RoutedEventArgs());
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDPrev()
    {
        Previous_Click(this, new RoutedEventArgs());
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDSeek(long positionMs)
    {
        var duration = _player.Snapshot.Duration;
        if (duration > TimeSpan.Zero)
            _player.Seek(TimeSpan.FromMilliseconds(Math.Clamp(positionMs, 0, (long)duration.TotalMilliseconds)));
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDSetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        VolumeSlider.Value = volume;
        _player.SetVolume(volume);
    }

    void IThreeDActions.ThreeDSetDisplayMode(string mode)
    {
        _displayMode = string.IsNullOrWhiteSpace(mode) ? "normal" : mode;
        ApplyDisplayMode();
        SaveSettings();
    }

    void IThreeDActions.ThreeDWindowDrag()
    {
        // The WebView2 child HWND owns the mouse, so WPF's DragMove() never sees the pressed
        // button. Ask the OS to start a caption drag instead, which uses the real mouse state.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    void IThreeDActions.ThreeDWindowResize(string edge)
    {
        // The WebView2 covers the whole client area, so the OS never sees the cursor on a resize
        // border. Map the page's edge gutters onto the matching non-client hit-test code and let
        // the OS run its own resize loop (same trick as the caption drag).
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var ht = edge switch
        {
            "left" => 10,          // HTLEFT
            "right" => 11,         // HTRIGHT
            "top" => 12,           // HTTOP
            "topleft" => 13,       // HTTOPLEFT
            "topright" => 14,      // HTTOPRIGHT
            "bottom" => 15,        // HTBOTTOM
            "bottomleft" => 16,    // HTBOTTOMLEFT
            "bottomright" => 17,   // HTBOTTOMRIGHT
            _ => 0
        };
        if (ht == 0) return;
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, new IntPtr(ht), IntPtr.Zero);
    }

    void IThreeDActions.ThreeDMinimize() => WindowState = WindowState.Minimized;

    void IThreeDActions.ThreeDMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void IThreeDActions.ThreeDClose()
    {
        _exitRequested = true;
        Close();
    }

    void IThreeDActions.ThreeDPlayTrack(string id)
    {
        var songId = id.StartsWith("track:", StringComparison.Ordinal) ? id[6..] : id;
        var row = _songs.FirstOrDefault(x => x.Song.Id == songId);
        if (row is null) return;
        var index = _songs.IndexOf(row);
        if (index >= 0) SelectSong(index, true);
        _threeDHost.PushState();
    }

    void IThreeDActions.ThreeDOnSetting(string key, JsonElement? value)
    {
        if (value is null) return;
        var v = value.Value;
        try
        {
            switch (key)
            {
                case "quality": _threeDQuality = v.GetString() ?? _threeDQuality; break;
                case "shelfMode": _threeDShelfMode = v.GetString() ?? _threeDShelfMode; break;
                case "shelfPresence": _threeDShelfPresence = v.GetString() ?? _threeDShelfPresence; break;
                case "cameraMode": _threeDCameraMode = v.GetString() ?? _threeDCameraMode; break;
                case "libraryKind": _threeDLibraryKind = v.GetString() ?? _threeDLibraryKind; break;
                case "coverPreset": _threeDCoverPreset = v.GetInt32(); break;
                case "bloom": _threeDBloom = v.GetBoolean(); break;
                case "lyrics": _threeDLyrics = v.GetBoolean(); break;
                case "shelfSize": _threeDShelfSize = v.GetDouble(); break;
                case "shelfOffsetX": _threeDShelfOffsetX = v.GetDouble(); break;
                case "shelfOffsetY": _threeDShelfOffsetY = v.GetDouble(); break;
                case "shelfAngleY": _threeDShelfAngleY = v.GetDouble(); break;
                case "shelfOpacity": _threeDShelfOpacity = v.GetDouble(); break;
                case "backgroundColor": _threeDBackgroundColor = v.GetString() ?? _threeDBackgroundColor; break;
                case "particleSize": _threeDParticleSize = v.GetDouble(); break;
                case "glow": _threeDGlow = v.GetDouble(); break;
                case "particleCount": _threeDParticleCount = v.GetDouble(); break;
                case "particleColorMode": _threeDParticleColorMode = v.GetInt32(); break;
                case "particleColor": _threeDParticleColor = v.GetString() ?? ""; break;
                default: return;
            }
            SaveSettings();
        }
        catch { }
    }

    /// <summary>Shows the lyric overlay above the CD (title at top, scrolling lyrics below).</summary>
    private void ApplyCdLyrics()
    {
        var cd = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase);
        var show = cd && _cdLyrics;
        CdLyricsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingInfo.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) UpdateLyricHighlight(force: true);
    }

    /// <summary>Removes the OS window frame (Windows 11 border + rounded corners) so no edge is drawn.</summary>
    private void ApplyCdChrome(bool cd)
    {
        BorderThickness = new Thickness(0);
        BorderBrush = Brushes.Transparent;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var corner = cd ? DwmcpDoNotRound : DwmcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var border = cd ? DwmwaColorNone : DwmwaColorDefault;
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch { /* 老版本系统不支持该属性 */ }
    }

    private readonly DropShadowEffect _discShadow = new()
    {
        BlurRadius = 44,
        ShadowDepth = 10,
        Direction = 270,
        Opacity = 0.55,
        Color = Colors.Black
    };

    private Size? _preCdSize;
    private double _preCdMinWidth = 340;
    private double _preCdMinHeight = 500;

    private void ApplyCdWindow(bool cd)
    {
        if (cd)
        {
            if (_preCdSize is null)
            {
                _preCdSize = new Size(Width, Height);
                _preCdMinWidth = MinWidth;
                _preCdMinHeight = MinHeight;
            }
            MinWidth = 320;
            MinHeight = 320;
            var side = Math.Max(360, Math.Round(Math.Min(_preCdSize.Value.Width, _preCdSize.Value.Height) * 2.0 / 3.0));
            Width = side;
            Height = side;
        }
        else if (_preCdSize is not null)
        {
            MinWidth = _preCdMinWidth;
            MinHeight = _preCdMinHeight;
            Width = _preCdSize.Value.Width;
            Height = _preCdSize.Value.Height;
            _preCdSize = null;
        }
    }

    /* ===================== cd mode window drag ===================== */

    private Point _stageDragStart;
    private bool _stageDragArmed;
    private bool _suppressDiscClick;

    private void Stage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _stageDragArmed = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase);
        _suppressDiscClick = false;
        _stageDragStart = e.GetPosition(this);
    }

    private void Stage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsVisualizerMode) ShowVisualizerControls();

        if (!_stageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _stageDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _stageDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _stageDragArmed = false;
        _suppressDiscClick = true;
        try { DragMove(); } catch { /* 非左键拖动时忽略 */ }
    }

    private void Stage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _stageDragArmed = false;

    /* ===================== cd mode disc zoom (wheel / pinch) ===================== */

    private void Stage_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase)) return;
        var factor = e.Delta > 0 ? CdZoomStep : 1.0 / CdZoomStep;
        SetCdZoom(_cdZoom * factor);
        e.Handled = true;
    }

    private void Stage_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        if (!string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel();
            return;
        }
        e.ManipulationContainer = Stage;
        e.Mode = ManipulationModes.Scale;
        e.Handled = true;
    }

    private void Stage_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        if (!string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase)) return;
        var s = e.DeltaManipulation.Scale;
        var factor = (s.X + s.Y) / 2.0;
        if (factor > 0.001) SetCdZoom(_cdZoom * factor);
        e.Handled = true;
    }

    /// <summary>Applies a CD-mode zoom factor (clamped) and persists it.</summary>
    private void SetCdZoom(double value)
    {
        var clamped = Math.Clamp(value, CdZoomMin, CdZoomMax);
        if (Math.Abs(clamped - _cdZoom) < 0.0005) return;
        _cdZoom = clamped;
        UpdateDiscRotation();
        SaveSettings();
    }

    private void CdResetZoom_Click(object sender, RoutedEventArgs e) => SetCdZoom(1.0);

    private void DiscPrev_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressDiscClick) { _suppressDiscClick = false; return; }
        Previous_Click(sender, e);
    }

    private void DiscPlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressDiscClick) { _suppressDiscClick = false; return; }
        Play_Click(sender, e);
    }

    private void DiscNext_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressDiscClick) { _suppressDiscClick = false; return; }
        Next_Click(sender, e);
    }

    /// <summary>While lyrics are shown the disc is behind them, so route clicks by region here too.</summary>
    private void CdLyrics_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressDiscClick) { _suppressDiscClick = false; return; }
        var width = CdLyricsOverlay.ActualWidth;
        if (width <= 0) return;
        var x = e.GetPosition(CdLyricsOverlay).X;
        if (x < width / 3) Previous_Click(sender, e);
        else if (x > width * 2 / 3) Next_Click(sender, e);
        else Play_Click(sender, e);
    }

    private void CdSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsSection = "appearance";
        Settings_Click(sender, e);
    }

    private void CdMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items.Count > 0 && menu.Items[0] is MenuItem item)
            item.Header = _cdLyrics ? "隐藏歌词" : "显示歌词";
    }

    private void CdLyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _cdLyrics = !_cdLyrics;
        if (_cdLyrics) RenderLyrics();
        ApplyCdLyrics();
        SaveSettings();
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        // First open: build a single reusable context menu offering the three modes.
        if (ViewModeButton.ContextMenu is null)
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateModeMenuItem("normal", "普通模式"));
            menu.Items.Add(CreateModeMenuItem("cd", "CD 唱片模式"));
            menu.Items.Add(CreateModeMenuItem("visualizer", "音频可视化"));
            menu.Items.Add(CreateModeMenuItem("mode3d", "3D 模式"));
            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = ViewModeButton;
            ViewModeButton.ContextMenu = menu;
        }

        var cm = ViewModeButton.ContextMenu;
        foreach (var item in cm.Items)
        {
            if (item is MenuItem mi)
                mi.IsChecked = string.Equals((string?)mi.Tag, _displayMode, StringComparison.OrdinalIgnoreCase);
        }

        cm.IsOpen = true;
        e.Handled = true;
    }

    private MenuItem CreateModeMenuItem(string mode, string header)
    {
        var item = new MenuItem
        {
            Header = header,
            Tag = mode,
            IsCheckable = true
        };
        item.Click += ModeMenuItem_Click;
        return item;
    }

    private void ModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string mode &&
            !string.Equals(mode, _displayMode, StringComparison.OrdinalIgnoreCase))
        {
            _displayMode = mode;
            ApplyDisplayMode();
            SaveSettings();
        }
        ViewModeButton.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }

    private void ExitCdMode_Click(object sender, RoutedEventArgs e)
    {
        _displayMode = "normal";
        ApplyDisplayMode();
        SaveSettings();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 && _currentOnline is null && _songs.Count > 0) { SelectSong(0, true); return; }
        if (_player.Snapshot.IsPlaying)
        {
            _player.Pause();
            PlayGlyphText.Text = "\uE768";
            return;
        }
        var duration = _player.Snapshot.Duration;
        if (duration > TimeSpan.Zero && _player.Snapshot.Position >= duration) _player.Seek(TimeSpan.Zero);
        _player.Play();
        _timer.Start();
        PlayGlyphText.Text = "\uE769";
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0) SelectSong(_currentIndex - 1, true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_songs.Count == 0) return;
        var next = _playbackMode == PlaybackMode.Shuffle ? PickRandomSongIndex() : (_currentIndex + 1) % _songs.Count;
        SelectSong(next, true);
    }

    private void AdvanceAfterEnded()
    {
        if (_currentOnline is not null)
        {
            _player.Stop();
            _currentOnline = null;
            PlayGlyphText.Text = "\uE768";
            return;
        }
        if (_playbackMode == PlaybackMode.RepeatOne && _currentIndex >= 0)
        {
            _player.Seek(TimeSpan.Zero);
            _player.Play();
            return;
        }
        if (_playbackMode == PlaybackMode.Sequential && _currentIndex >= _songs.Count - 1)
        {
            _player.Stop();
            PlayGlyphText.Text = "\uE768";
            return;
        }
        Next_Click(this, new RoutedEventArgs());
    }

    private int PickRandomSongIndex()
    {
        if (_songs.Count < 2) return 0;
        var next = Random.Shared.Next(_songs.Count);
        while (next == _currentIndex) next = Random.Shared.Next(_songs.Count);
        return next;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        _playbackMode = _playbackMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.RepeatOne,
            _ => PlaybackMode.Sequential
        };
        ModeGlyphText.Text = ModeGlyph(_playbackMode);
        _player.SetMode(_playbackMode);
    }

    private static string? BuildMetaText(Song song)
    {
        if (song.BitDepth > 0 && song.SampleRate > 0) return $"{song.Format} · {song.BitDepth}bit/{song.SampleRate / 1000.0:0.#}kHz";
        if (song.Bitrate > 0) return $"{song.Format} · {song.Bitrate} kbps";
        return string.IsNullOrWhiteSpace(song.Format) ? null : song.Format;
    }

    private static string ModeGlyph(PlaybackMode mode) => mode switch
    {
        PlaybackMode.Sequential => "\uE8EE",
        PlaybackMode.Shuffle => "\uE8B1",
        _ => "\uE8ED"
    };

    private void SongList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not SongRow row) return;
        if (row.Online is not null) { _ = PlayOnlineAsync(row); return; }
        var index = _songs.IndexOf(row);
        if (index >= 0) SelectSong(index, true);
    }

    private void LibraryGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LibraryItem card) PlayLibraryCard(card);
    }

    private void PlayLibraryCard(LibraryItem card)
    {
        if (_view == "playlists")
        {
            var playlist = _playlists.FirstOrDefault(p => p.Name == card.Name);
            if (playlist.Id != 0) { _view = $"playlist:{playlist.Id}"; RenderLibrary(); }
            return;
        }
        var first = _songs.FirstOrDefault(x => (_view == "albums" ? x.Song.Album : x.Song.Artist) == card.Name);
        if (first is null) return;
        var index = _songs.IndexOf(first);
        if (index >= 0) SelectSong(index, true);
    }

    private void SongList_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is not SongRow row) return;
        list.SelectedItem = row;
        if (row.Online is not null)
        {
            BuildOnlineContextMenu(list, row);
            e.Handled = true;
            return;
        }
        if (string.IsNullOrEmpty(row.Song.Id)) { e.Handled = true; return; }
        var menu = new ContextMenu();
        var favorite = new MenuItem { Header = row.IsFavorite ? "取消收藏" : "添加到收藏" };
        favorite.Click += (_, _) => ToggleFavorite(row);
        menu.Items.Add(favorite);
        var play = new MenuItem { Header = "播放" };
        play.Click += (_, _) => { var i = _songs.IndexOf(row); if (i >= 0) SelectSong(i, true); };
        menu.Items.Add(play);
        var add = new MenuItem { Header = "添加到播放列表" };
        foreach (var playlist in _playlists)
        {
            var child = new MenuItem { Header = playlist.Name };
            var id = playlist.Id;
            child.Click += (_, _) =>
            {
                _database.AddSongToPlaylist(id, row.Song.Id);
                if (_playlistSongs.TryGetValue(id, out var set)) set.Add(row.Song.Id);
                Toast($"已添加到「{playlist.Name}」");
            };
            add.Items.Add(child);
        }
        add.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建播放列表…" };
        create.Click += (_, _) => CreatePlaylistWithSong(row);
        add.Items.Add(create);
        menu.Items.Add(add);
        if (_view.StartsWith("playlist:", StringComparison.Ordinal) && int.TryParse(_view[9..], out var currentPlaylist))
        {
            var remove = new MenuItem { Header = "从当前播放列表移除" };
            remove.Click += (_, _) =>
            {
                _database.RemoveSongFromPlaylist(currentPlaylist, row.Song.Id);
                if (_playlistSongs.TryGetValue(currentPlaylist, out var set)) set.Remove(row.Song.Id);
                RenderLibrary();
            };
            menu.Items.Add(remove);
        }
        menu.PlacementTarget = list;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void BuildOnlineContextMenu(ListBox list, SongRow row)
    {
        var track = row.Online!;
        var menu = new ContextMenu();
        var audition = new MenuItem { Header = "在线试听" };
        audition.Click += (_, _) => _ = PlayOnlineAsync(row);
        menu.Items.Add(audition);
        var download = new MenuItem { Header = "下载到本地", IsEnabled = track.Downloadable };
        download.Click += (_, _) => _ = DownloadOnlineAsync(row, false);
        menu.Items.Add(download);
        var downloadPlay = new MenuItem { Header = "下载并播放", IsEnabled = track.Downloadable };
        downloadPlay.Click += (_, _) => _ = DownloadOnlineAsync(row, true);
        menu.Items.Add(downloadPlay);
        menu.PlacementTarget = list;
        menu.IsOpen = true;
    }

    private void ToggleFavorite(SongRow row)
    {
        var favorite = !row.IsFavorite;
        _database.SetFavorite(row.Song.Id, favorite);
        row.IsFavorite = favorite;
        if (favorite) _favoriteIds.Add(row.Song.Id); else _favoriteIds.Remove(row.Song.Id);
        SongList.Items.Refresh();
        if (LibraryList.ItemsSource is not null) LibraryList.Items.Refresh();
        if (_view == "favorites") RenderLibrary();
    }

    private void CreatePlaylistWithSong(SongRow row)
    {
        var dialog = new Window
        {
            Owner = this, Title = "新建播放列表", Width = 360, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            Background = ResBrush("SurfaceBrush"),
            Foreground = ResBrush("TextPrimaryBrush")
        };
        var box = new TextBox { Text = "我的歌单", Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 16) };
        var boxHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = box };
        var ok = new Button { Content = "创建", Style = (Style)FindResource("PrimaryActionButton"), Padding = new Thickness(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) { Toast("请输入播放列表名称"); return; }
            var id = _database.CreatePlaylist(name);
            _database.AddSongToPlaylist(id, row.Song.Id);
            RefreshLibraryMeta();
            dialog.Close();
            Toast($"已创建「{name}」并加入歌曲");
        };
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = "播放列表名称" });
        layout.Children.Add(boxHost);
        layout.Children.Add(ok);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        PlayGlyphText.Text = "\uE768";
        _songs.Clear();
        _currentIndex = -1;
        RenderQueue();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom };
        AddSortItem(menu, 0, "默认顺序");
        AddSortItem(menu, 1, "歌曲名称");
        AddSortItem(menu, 2, "歌手");
        AddSortItem(menu, 3, "专辑");
        AddSortItem(menu, 4, "音质");
        AddSortItem(menu, 5, "时长");
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddSortItem(ContextMenu menu, int mode, string label)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _sortMode == mode };
        item.Click += (_, _) => ApplySort(mode);
        menu.Items.Add(item);
    }

    private void ApplySort(int mode)
    {
        if (mode == _sortMode) return;
        var current = _currentIndex >= 0 && _currentIndex < _songs.Count ? _songs[_currentIndex] : null;
        var ordered = (mode switch
        {
            1 => _songs.OrderBy(x => x.Title, StringComparer.CurrentCulture),
            2 => _songs.OrderBy(x => x.Artist, StringComparer.CurrentCulture),
            3 => _songs.OrderBy(x => x.Song.Album, StringComparer.CurrentCulture),
            4 => _songs.OrderByDescending(x => QualityRank(x.Song.Quality)).ThenBy(x => x.Title, StringComparer.CurrentCulture),
            5 => _songs.OrderBy(x => x.Song.DurationMs <= 0 ? long.MaxValue : x.Song.DurationMs),
            _ => _songs.OrderBy(x => x.Index)
        }).ToList();
        _sortMode = mode;
        _songs.Clear();
        foreach (var row in ordered) _songs.Add(row);
        _currentIndex = current is null ? -1 : _songs.IndexOf(current);
        RenderQueue();
    }

    private static int QualityRank(string quality) => quality switch
    {
        "Hi-Res" => 5,
        "无损" => 4,
        "高清" => 3,
        "在线·可下载" => 2,
        "普通" => 1,
        _ => 0
    };

    private void ApplyQualityVisual(string quality)
    {
        var (bg, fg) = quality switch
        {
            "高清" => ("QualityHighBgBrush", "QualityHighFgBrush"),
            "无损" => ("QualityLosslessBgBrush", "QualityLosslessFgBrush"),
            "Hi-Res" => ("QualityHiresBgBrush", "QualityHiresFgBrush"),
            "在线·可下载" or "在线·试听" => ("QualityOnlineBgBrush", "QualityOnlineFgBrush"),
            _ => ("QualityNormalBgBrush", "QualityNormalFgBrush")
        };
        QualityChip.Background = (Brush)FindResource(bg);
        QualityText.Foreground = (Brush)FindResource(fg);

        // The visualizer backdrop is always dark, so colour the quality text with the
        // fixed dark-palette accents instead of the theme-swapped quality brushes.
        VizQualityText.Text = quality;
        var vizColor = quality switch
        {
            "高清" => Color.FromRgb(0x8F, 0xB8, 0xFF),
            "无损" => Color.FromRgb(0x6E, 0xE7, 0xB7),
            "Hi-Res" => Color.FromRgb(0xFC, 0xD3, 0x4D),
            "在线·可下载" or "在线·试听" => Color.FromRgb(0xA9, 0xBB, 0xFF),
            _ => Color.FromRgb(0xB9, 0xC0, 0xD0)
        };
        var vizBrush = new SolidColorBrush(vizColor);
        vizBrush.Freeze();
        VizQualityText.Foreground = vizBrush;
    }

    /* ===================== volume / progress ===================== */

    private void Volume_Click(object sender, RoutedEventArgs e) => VolumePopup.IsOpen = true;

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player.SetVolume(e.NewValue);
        if (VolumeText is not null) VolumeText.Text = $"{e.NewValue:P0}";
    }

    /* ===================== visualizer style switch ===================== */

    private void VizStyle_Click(object sender, RoutedEventArgs e)
    {
        RebuildVizStyleMenu();
        VizStylePopup.IsOpen = true;
    }

    /// <summary>Builds the popup list from the renderers currently registered in the engine.</summary>
    private void RebuildVizStyleMenu()
    {
        VizStylePanel.Children.Clear();
        var iconFont = (FontFamily)FindResource("IconFont");
        var checkBrush = (Brush)FindResource("AccentFillColorDefaultBrush");
        var textBrush = (Brush)FindResource("TextFillColorPrimaryBrush");

        foreach (var r in _visualizerEngine.AllRenderers)
        {
            bool active = string.Equals(r.StyleId, _visualizerStyle, StringComparison.OrdinalIgnoreCase);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "\uE73E",
                FontFamily = iconFont,
                FontSize = 12,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? checkBrush : Brushes.Transparent,
            });
            row.Children.Add(new TextBlock
            {
                Text = r.DisplayName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = textBrush,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            });

            var item = new Wpf.Ui.Controls.Button
            {
                Content = row,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 14, 6),
                Margin = new Thickness(0, 1, 0, 1),
            };
            string id = r.StyleId;
            item.Click += (_, _) =>
            {
                _visualizerStyle = id;
                _visualizerEngine.SetStyle(id);
                SaveSettings();
                RebuildVizStyleMenu();
                VizStylePopup.IsOpen = false;
            };
            VizStylePanel.Children.Add(item);
        }

        var current = _visualizerEngine.AllRenderers
            .FirstOrDefault(r => string.Equals(r.StyleId, _visualizerStyle, StringComparison.OrdinalIgnoreCase));
        VizStyleButton.ToolTip = current is null ? "可视化风格" : $"可视化风格：{current.DisplayName}";
    }

    private void Progress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var duration = _player.Snapshot.Duration;
        if (!_draggingProgress && duration > TimeSpan.Zero) _player.Seek(TimeSpan.FromMilliseconds(e.NewValue * duration.TotalMilliseconds));
    }

    private void UpdateDuration()
    {
        if (_player.Snapshot.Duration > TimeSpan.Zero) ProgressSlider.Maximum = 1;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        UpdateDiscRotation();
        var duration = _player.Snapshot.Duration;
        if (duration <= TimeSpan.Zero) return;
        var position = _player.Snapshot.Position;
        _draggingProgress = true;
        ProgressSlider.Value = position.TotalMilliseconds / duration.TotalMilliseconds;
        CurrentTime.Text = position.ToString(@"mm\:ss");
        TotalTime.Text = duration.ToString(@"mm\:ss");
        _draggingProgress = false;
        UpdateLyricHighlight();
    }

    /* ===================== lyrics ===================== */

    private void FetchLyrics_Click(object sender, RoutedEventArgs e) => _ = RefreshOnlineLyricsAsync();

    private void LyricsMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var entry in menu.Items)
            if (entry is MenuItem item && (item.Header as string)?.Contains("歌词") == true)
                item.IsEnabled = _currentIndex >= 0;
    }

    /// <summary>Right-click "返回播放页面": leaves the lyrics view in both page and visualizer modes.</summary>
    private void BackToPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (IsVisualizerMode && _vizLyrics)
        {
            _vizLyrics = false;
            ApplyVisualizerLyrics();
            SaveSettings();
            return;
        }
        _page = null;
        ApplyPage();
    }

    private async Task RefreshOnlineLyricsAsync()
    {
        if (_currentIndex < 0) return;
        var songId = _songs[_currentIndex].Song.Id;
        try { File.Delete(_lyricsProvider.CachePath(songId)); } catch { }
        _lyricLines.Clear();
        _lyricLines.Add(new LyricLine(0, "正在获取歌词…"));
        ClearLyricBlocks();
        BuildLyricBlocks();
        await LoadOnlineLyricsAsync(songId);
    }

    private void RenderLyrics()
    {
        ClearLyricBlocks();
        _lyricLines.Clear();
        if (_currentIndex < 0) return;
        var song = _songs[_currentIndex].Song;
        LyricsHead.Text = song.Title;
        LyricsSub.Text = song.Artist;
        var lrc = Path.ChangeExtension(song.Path, ".lrc");
        var lines = File.Exists(lrc) ? LrcParser.Parse(ReadTextAuto(lrc)) : _lyricsProvider.ReadCache(song.Id);
        if (lines.Count == 0)
        {
            _lyricLines.Add(new LyricLine(0, "正在获取歌词…"));
            _lyricLines.Add(new LyricLine(0, "也可在歌曲同目录放置同名 .lrc 文件"));
            _ = LoadOnlineLyricsAsync(song.Id);
        }
        else _lyricLines.AddRange(lines);
        BuildLyricBlocks();
        UpdateLyricHighlight(force: true);
    }

    private static string ReadTextAuto(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes); }
    }

    private void ClearLyricBlocks()
    {
        LyricsPanel.Children.Clear();
        CdLyricsPanel.Children.Clear();
        VizLyricsPanel.Children.Clear();
        _lyricBlocks.Clear();
        _cdLyricBlocks.Clear();
        _vizLyricBlocks.Clear();
        _lastLyricIndex = -1;
    }

    private void BuildLyricBlocks()
    {
        foreach (var line in _lyricLines)
        {
            _lyricBlocks.Add(CreateLyricBlock(line, LyricsPanel));
            _cdLyricBlocks.Add(CreateLyricBlock(line, CdLyricsPanel));
            _vizLyricBlocks.Add(CreateLyricBlock(line, VizLyricsPanel));
        }
        _threeDHost.PushLyrics();
    }

    private TextBlock CreateLyricBlock(LyricLine line, Panel host)
    {
        var family = string.IsNullOrWhiteSpace(_lyricFontFamily)
            ? (Application.Current?.TryFindResource("AppFont") as FontFamily ?? SystemFonts.MessageFontFamily)
            : new FontFamily(_lyricFontFamily);
        var block = new TextBlock
        {
            Text = line.Text,
            Foreground = ReferenceEquals(host, VizLyricsPanel) ? VizLyricInactiveBrush : ResBrush("TextMutedBrush"),
            FontFamily = family,
            FontSize = _lyricsFontSize,
            Margin = new Thickness(8, 2, 8, 2),
            MaxWidth = 760,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        host.Children.Add(block);
        return block;
    }

    private async Task LoadOnlineLyricsAsync(string songId)
    {
        try
        {
            if (_currentIndex < 0 || _songs[_currentIndex].Song.Id != songId) return;
            var lines = await _lyricsProvider.FetchAsync(_songs[_currentIndex].Song);
            if (lines.Count == 0) return;
            if (_currentIndex < 0 || _songs[_currentIndex].Song.Id != songId) return;
            _lyricLines.Clear();
            _lyricLines.AddRange(lines);
            ClearLyricBlocks();
            BuildLyricBlocks();
            UpdateLyricHighlight(force: true);
        }
        catch { }
    }

    private void UpdateLyricHighlight(bool force = false)
    {
        if (_lyricLines.Count == 0) return;
        var current = 0;
        var position = _player.Snapshot.Position.TotalMilliseconds;
        for (var i = 0; i < _lyricLines.Count; i++) if (_lyricLines[i].TimeMs <= position) current = i;
        var activeBrush = _lyricActiveColor is { } activeColor ? new SolidColorBrush(activeColor) : ResBrush("AccentBrush");
        var inactiveBrush = _lyricInactiveColor is { } inactiveColor ? new SolidColorBrush(inactiveColor) : ResBrush("TextMutedBrush");
        var vizInactiveBrush = _lyricInactiveColor is { } vizColor ? new SolidColorBrush(vizColor) : VizLyricInactiveBrush;
        for (var i = 0; i < _lyricBlocks.Count; i++)
        {
            var active = i == current;
            StyleLyricBlock(_lyricBlocks[i], active, activeBrush, inactiveBrush);
            if (i < _cdLyricBlocks.Count) StyleLyricBlock(_cdLyricBlocks[i], active, activeBrush, inactiveBrush);
            if (i < _vizLyricBlocks.Count) StyleLyricBlock(_vizLyricBlocks[i], active, activeBrush, vizInactiveBrush);
        }

        var changed = force || current != _lastLyricIndex;
        _lastLyricIndex = current;
        if (!changed) return;
        _threeDHost.PushLyricIndex();
        if (current < _lyricBlocks.Count && LyricsPage.Visibility == Visibility.Visible)
            CenterActiveLyric(LyricsView, _lyricBlocks[current]);
        if (current < _cdLyricBlocks.Count && CdLyricsOverlay.Visibility == Visibility.Visible)
            CenterActiveLyric(CdLyricsView, _cdLyricBlocks[current]);
        if (current < _vizLyricBlocks.Count && VisualizerLyricsOverlay.Visibility == Visibility.Visible)
            CenterActiveLyric(VizLyricsView, _vizLyricBlocks[current]);
    }

    /* ===================== equalizer ===================== */

    private void ApplyEqToEngine()
    {
        _player.SetEqualizerEnabled(_eqEnabled);
        for (var i = 0; i < _eqGains.Length; i++)
            _player.SetEqualizerBand(i, _eqGains[i]);
    }

    private static readonly string[] EqBandFrequencies = { "80", "125", "250", "500", "1k", "2k", "4k", "8k", "12k", "16k" };

    /// <summary>Builds the equalizer settings card (enable switch, 10 bands and presets).</summary>
    private UIElement BuildEqPanel()
    {
        var root = new StackPanel();

        _eqEnableToggle = new ToggleButton { Style = (Style)FindResource("SwitchToggle"), IsChecked = _eqEnabled };
        _eqEnableToggle.Click += (_, _) =>
        {
            _eqEnabled = _eqEnableToggle.IsChecked == true;
            ApplyEqToEngine();
            UpdateEqBandSlidersEnabled();
            SaveSettings();
        };
        root.Children.Add(Field("启用均衡器", "仅作用于播放输出，不修改源文件", _eqEnableToggle));

        var bands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        BuildEqBands(bands);
        root.Children.Add(bands);

        var presets = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        BuildEqPresets(presets);
        root.Children.Add(presets);

        return root;
    }

    private void UpdateEqBandSlidersEnabled()
    {
        foreach (var slider in _eqSliders) slider.IsEnabled = _eqEnabled;
    }

    private void BuildEqBands(Panel host)
    {
        host.Children.Clear();
        _eqSliders.Clear();
        _eqGainTexts.Clear();
        for (var band = 0; band < _eqGains.Length; band++)
        {
            var gain = _eqGains[band];
            var column = new StackPanel { Orientation = Orientation.Vertical, Width = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (band == _eqGains.Length - 1) column.Margin = new Thickness(0, 0, 0, 0);

            var gainText = new TextBlock
            {
                Text = $"{gain:0}",
                FontSize = 10,
                Foreground = ResBrush("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Height = 120,
                Minimum = -12,
                Maximum = 12,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = gain,
                IsEnabled = _eqEnabled,
                HorizontalAlignment = HorizontalAlignment.Center,
                SmallChange = 1,
                LargeChange = 2
            };
            var bandIndex = band;
            slider.ValueChanged += (_, args) =>
            {
                _eqGains[bandIndex] = args.NewValue;
                gainText.Text = $"{args.NewValue:0}";
                _player.SetEqualizerBand(bandIndex, args.NewValue);
                if (_eqEnabled && !_suppressEqSave) SaveSettings();
            };

            var freqText = new TextBlock
            {
                Text = EqBandFrequencies[band],
                FontSize = 9,
                Foreground = ResBrush("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            _eqSliders.Add(slider);
            _eqGainTexts.Add(gainText);
            column.Children.Add(gainText);
            column.Children.Add(slider);
            column.Children.Add(freqText);
            host.Children.Add(column);
        }
    }

    private void BuildEqPresets(Panel host)
    {
        host.Children.Clear();
        // Preset buttons
        var presets = new (string Name, double[] Gains)[]
        {
            ("Flat", new double[10]),
            ("摇滚", new[] { 5.0, 4.0, 3.0, 1.0, -1.0, -1.0, 1.0, 3.0, 4.0, 5.0 }),
            ("流行", new[] { -1.0, 2.0, 4.0, 5.0, 4.0, 2.0, 0.0, -1.0, -1.0, 0.0 }),
            ("爵士", new[] { 3.0, 2.0, 1.0, 2.0, -1.0, -1.0, 0.0, 1.0, 2.0, 3.0 }),
            ("古典", new[] { 4.0, 3.0, 2.0, 0.0, 0.0, 0.0, 0.0, 2.0, 3.0, 4.0 }),
            ("人声", new[] { -2.0, -1.0, 0.0, 2.0, 4.0, 4.0, 3.0, 1.0, 0.0, -1.0 }),
            ("低音增强", new[] { 7.0, 6.0, 4.0, 2.0, 0.0, -1.0, -2.0, -2.0, -3.0, -4.0 }),
            ("高音增强", new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 3.0, 4.0, 6.0, 7.0 }),
            ("电子", new[] { 5.0, 4.0, 2.0, 0.0, -2.0, 1.0, 3.0, 4.0, 4.0, 3.0 }),
            ("嘻哈", new[] { 5.0, 4.0, 3.0, 1.0, -1.0, -1.0, -2.0, 1.0, 3.0, 5.0 }),
            ("舞曲", new[] { 5.0, 4.0, 3.0, 0.0, -2.0, 0.0, 2.0, 4.0, 5.0, 5.0 }),
            ("明亮", new[] { 0.0, 0.0, 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 5.0, 4.0 }),
            ("柔和", new[] { 0.0, 1.0, 2.0, 2.0, 2.0, 1.0, 0.0, -1.0, -1.0, 0.0 }),
            ("蓝调", new[] { 3.0, 3.0, 2.0, 1.0, 0.0, -1.0, -1.0, 1.0, 2.0, 3.0 }),
            ("电影", new[] { 4.0, 4.0, 3.0, 1.0, 0.0, -1.0, 0.0, 2.0, 4.0, 5.0 }),
        };
        foreach (var preset in presets)
        {
            var name = preset.Name;
            var gains = preset.Gains;
            var btn = new Button
            {
                Content = name,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 11,
                Style = (Style)FindResource("GhostButton")
            };
            btn.Click += (_, _) => ApplyEqPreset(gains);
            host.Children.Add(btn);
        }
    }

    private void ApplyEqPreset(double[] gains)
    {
        for (var i = 0; i < Math.Min(gains.Length, _eqGains.Length); i++)
        {
            _eqGains[i] = gains[i];
            _player.SetEqualizerBand(i, gains[i]);
        }
        _eqEnabled = true;
        ApplyEqToEngine();

        // Refresh UI
        _suppressEqSave = true;
        try
        {
            if (_eqEnableToggle is not null) _eqEnableToggle.IsChecked = true;
            UpdateEqBandSlidersEnabled();            for (var i = 0; i < _eqGains.Length && i < _eqSliders.Count; i++)
            {
                _eqSliders[i].Value = _eqGains[i];
                _eqGainTexts[i].Text = $"{_eqGains[i]:0}";
            }
        }
        finally { _suppressEqSave = false; }

        SaveSettings();
    }

    /// <summary>Scrolls the lyric view so the active line is vertically centred.</summary>
    private void CenterActiveLyric(ScrollViewer view, TextBlock block)
    {
        if (view is null || block is null) return;
        if (view.ViewportHeight <= 0 || block.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() => CenterActiveLyric(view, block)), DispatcherPriority.Loaded);
            return;
        }
        // Half-viewport padding above and below lets the first/last line sit at the centre too.
        if (block.Parent is FrameworkElement panel)
        {
            var margin = new Thickness(0, view.ViewportHeight / 2, 0, view.ViewportHeight / 2);
            if (!Equals(panel.Margin, margin))
            {
                panel.Margin = margin;
                view.UpdateLayout();
            }
        }
        try
        {
            var point = block.TransformToAncestor(view).Transform(new Point(0, 0));
            var target = view.VerticalOffset + point.Y - Math.Max(0, (view.ViewportHeight - block.ActualHeight) / 2);
            view.ScrollToVerticalOffset(Math.Max(0, target));
        }
        catch { try { block.BringIntoView(); } catch { } }
    }

    private void StyleLyricBlock(TextBlock block, bool active, Brush accent, Brush muted)
    {
        block.Foreground = active ? accent : muted;
        block.FontSize = active ? _lyricsFontSize * 1.25 : _lyricsFontSize;
        block.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        block.Opacity = active || !_dimLyrics ? 1 : .5;
    }

    /* ===================== online search ===================== */

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Online search is explicit (button / Enter) — typing must not fire a request.
        if (_onlineMode) return;
        RenderLibrary();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _onlineMode)
        {
            QueueOnlineSearch();
            e.Handled = true;
        }
    }

    private void OnlineSearchButton_Click(object sender, RoutedEventArgs e) => QueueOnlineSearch();

    private void UpdateOnlineButton()
    {
        OnlineSourceBox.Visibility = _onlineMode ? Visibility.Visible : Visibility.Collapsed;
        OnlineSearchButton.Visibility = _onlineMode ? Visibility.Visible : Visibility.Collapsed;
        LibraryTabs.Visibility = _onlineMode ? Visibility.Collapsed : Visibility.Visible;
        LibraryTitle.Text = _onlineMode ? "在线音乐库" : "音乐库";
        SearchBox.PlaceholderText = _onlineMode ? "输入关键词后点击搜索" : "搜索";
        if (!_onlineMode) SetOnlineSearching(false);
    }

    private void PopulateOnlineSources()
    {
        OnlineSourceBox.Items.Clear();
        OnlineSourceBox.Items.Add(new ComboBoxItem { Content = "全部", Tag = OnlineMusicService.AllProviders });
        foreach (var provider in _online.Providers)
            OnlineSourceBox.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.Key });
        SelectOnlineSource(_onlineProvider);
    }

    private void SelectOnlineSource(string key)
    {
        _suppressOnlineSourceEvent = true;
        try
        {
            foreach (var item in OnlineSourceBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
                {
                    OnlineSourceBox.SelectedItem = item;
                    return;
                }
            }
            if (OnlineSourceBox.Items.Count > 0) OnlineSourceBox.SelectedIndex = 0;
        }
        finally { _suppressOnlineSourceEvent = false; }
    }

    private void OnlineSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOnlineSourceEvent || OnlineSourceBox.SelectedItem is not ComboBoxItem item) return;
        _onlineProvider = item.Tag as string ?? OnlineMusicService.AllProviders;
        SaveSettings();
    }

    private void ConfigureOnlineProviders()
    {
        _jamendo.ClientId = _jamendoClientId;
        _gdStudio.Source = _gdSource;
        _gdStudio.Bitrate = _gdBitrate;
        _audius.AppName = string.IsNullOrWhiteSpace(_audiusAppName) ? "XstarMusic" : _audiusAppName;
    }

    private void QueueOnlineSearch()
    {
        _onlineSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _onlineSearch = cts;
        var generation = ++_onlineSearchGeneration;
        _ = OnlineSearchAsync(cts.Token, generation);
    }

    private void SetOnlineSearching(bool searching)
    {
        var visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        OnlineSearchRing.Visibility = visibility;
        OnlineSearchStatus.Visibility = visibility;
    }

    private async Task OnlineSearchAsync(CancellationToken cancellationToken, int generation)
    {
        var query = SearchBox.Text.Trim();
        if (_onlineMode && query.Length >= 1) ShowSearchingPlaceholder(query, generation);

        try
        {
            if (query.Length < 1)
            {
                _onlineResults.Clear();
                _onlineResults.Add(new SongRow(new Song { Title = "在线音乐库", Artist = "输入关键词后点击「搜索」" }, 1) { IsStatus = true });
                RenderLibrary();
                return;
            }

            var selected = _online.Find(_onlineProvider);
            if (selected is not null && !selected.IsConfigured)
            {
                _onlineResults.Clear();
                _onlineResults.Add(new SongRow(new Song { Title = "音源未配置", Artist = $"请先在设置中完成 {selected.DisplayName} 的配置" }, 1) { IsStatus = true });
                RenderLibrary();
                return;
            }

            await Task.Delay(350, cancellationToken);
            var tracks = await _online.SearchAsync(query, _onlineProvider, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            _onlineResults.Clear();
            var index = 1;
            foreach (var track in tracks)
            {
                var source = _online.Find(track.ProviderKey)?.DisplayName ?? "在线";
                var song = new Song
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    DurationMs = track.DurationMs,
                    Format = source,
                    Quality = track.Downloadable ? "在线·可下载" : "在线·试听"
                };
                _onlineResults.Add(new SongRow(song, index++) { Online = track });
            }
            if (_onlineResults.Count == 0) _onlineResults.Add(new SongRow(new Song { Title = "无搜索结果", Artist = query }, 1) { IsStatus = true });
            RenderLibrary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _onlineSearchGeneration) Toast("在线搜索失败：" + ex.Message);
        }
        finally
        {
            if (generation == _onlineSearchGeneration) SetOnlineSearching(false);
        }
    }

    /// <summary>Shows an inline "搜索中…" row plus the header spinner while a request is in flight.</summary>
    private void ShowSearchingPlaceholder(string query, int generation)
    {
        if (generation != _onlineSearchGeneration) return;
        SetOnlineSearching(true);
        _onlineResults.Clear();
        _onlineResults.Add(new SongRow(new Song { Title = "搜索中…", Artist = query }, 1) { IsStatus = true });
        RenderLibrary();
    }

    /// <summary>Streams an online track through the player engine, falling back to a cached copy.</summary>
    private async Task PlayOnlineAsync(SongRow row)
    {
        if (row.Online is not { } track) return;
        try
        {
            var url = await _online.ResolveStreamUrlAsync(track);
            if (string.IsNullOrWhiteSpace(url))
            {
                Toast($"无法播放「{track.Title}」：暂无可用音源（可能受版权或地区限制）");
                return;
            }

            var quality = row.Song.Quality;
            var song = BuildOnlineSong(track, url, quality);

            _currentIndex = -1;
            _currentOnline = song;
            _database.AddHistory(song.Id, DateTime.UtcNow.Ticks);
            UpdateNowPlayingUi(song);
            _lyricLines.Clear();
            RenderLyrics();

            _suppressPlaybackError = true;
            var opened = _player.Load(song);
            _suppressPlaybackError = false;

            if (!opened)
            {
                // Some sources block direct streaming (hotlink protection); cache and play locally.
                var cached = await CacheOnlineAsync(track);
                if (cached is null)
                {
                    Toast($"无法播放「{track.Title}」：该音源暂时不可用");
                    PlayGlyphText.Text = "\uE768";
                    return;
                }
                song = BuildOnlineSong(track, cached, quality);
                _currentOnline = song;
                _player.Load(song);
            }

            _player.Play();
            PlayGlyphText.Text = "\uE769";
            AnimateNowPlaying();
            _timer.Start();
            UpdateDiscRotation();
        }
        catch (Exception ex) { Toast("在线播放失败：" + ex.Message); }
    }

    private Song BuildOnlineSong(OnlineTrack track, string path, string quality) => new()
    {
        Id = track.Id,
        Path = path,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        DurationMs = track.DurationMs,
        Format = _online.Find(track.ProviderKey)?.DisplayName ?? "在线",
        Quality = quality
    };

    private async Task<string?> CacheOnlineAsync(OnlineTrack track)
    {
        try
        {
            var cacheDirectory = Path.Combine(_dataDirectory, "online-cache");
            return await _online.DownloadAsync(track, cacheDirectory);
        }
        catch { return null; }
    }

    private void OnlineAudition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SongRow row } && row.Online is not null) _ = PlayOnlineAsync(row);
    }

    private void OnlineDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SongRow row } && row.Online is not null) _ = DownloadOnlineAsync(row, false);
    }

    private async Task DownloadOnlineAsync(SongRow row, bool playAfter)
    {
        if (row.Online is not { } track) return;
        if (!EnsureDownloadFolder()) return;

        var folder = _downloadFolder;
        try
        {
            Toast($"开始下载：{track.Artist} - {track.Title}");
            var path = await _online.DownloadAsync(track, folder);
            _onlineMode = false;
            UpdateOnlineButton();
            await ScanAsync();
            if (playAfter)
            {
                var index = _songs.ToList().FindIndex(r => r.Song.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) SelectSong(index, true);
            }
            Toast("下载完成：" + Path.GetFileName(path));
        }
        catch (Exception ex) { Toast("下载失败：" + ex.Message); }
    }

    /// <summary>
    /// Ensures a download folder is configured. When none is set it fills in a default, registers
    /// it as a scanned music folder and opens Settings → 在线 so the user can confirm or change it.
    /// Returns false when the caller should abort the download.
    /// </summary>
    private bool EnsureDownloadFolder()
    {
        if (!string.IsNullOrWhiteSpace(_downloadFolder))
        {
            RegisterDownloadFolder(_downloadFolder);
            return true;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "XSTAR");
        _downloadFolder = fallback;
        RegisterDownloadFolder(fallback, scan: true);
        SaveSettings();
        OpenOnlineSettings();
        Toast($"尚未设置下载目录，已默认使用：\n{fallback}\n\n该目录已加入扫描文件夹，可在「设置 → 在线」中修改后重新下载。");
        return false;
    }

    private void OpenOnlineSettings()
    {
        OpenSettings();
        BuildSettings("online");
    }

    /// <summary>Adds the download folder to the scanned library folders so downloads get indexed.</summary>
    private void RegisterDownloadFolder(string folder, bool scan = false)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try { Directory.CreateDirectory(folder); } catch { return; }
        if (_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _folders.Add(folder);
        ConfigureWatchers();
        SaveSettings();
        if (scan) _ = ScanAsync();
    }

    /* ===================== overlays ===================== */

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        var menu = LibraryButton.ContextMenu;
        if (menu is null) { MyLibrary_Click(sender, e); return; }
        menu.PlacementTarget = LibraryButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -60;
        menu.IsOpen = true;
    }

    private void MyLibrary_Click(object sender, RoutedEventArgs e) => OpenLibrary(online: false);

    private void OnlineLibrary_Click(object sender, RoutedEventArgs e) => OpenLibrary(online: true);

    /// <summary>Opens 我的音乐库 / 在线音乐库 as a full page, styled like the playlist page.</summary>
    private void OpenLibrary(bool online)
    {
        _onlineMode = online;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Text = "";
        if (online && _onlineResults.Count == 0)
            _onlineResults.Add(new SongRow(new Song { Title = "在线音乐库", Artist = "输入关键词后点击「搜索」" }, 1) { IsStatus = true });
        _page = online ? "online" : "library";
        Stage.Visibility = Visibility.Visible;
        UpdateOnlineButton();
        ApplyPage();
        SyncTitleBarBackdrop();
    }

    private void LibraryClose_Click(object sender, RoutedEventArgs e)
    {
        _page = null;
        ApplyPage();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
        BuildSettings(_settingsSection);
    }

    /// <summary>Opens the settings overlay, hiding the stage.</summary>
    private void OpenSettings()
    {
        _page = null;
        ApplyPage();
        SettingsOverlay.Visibility = Visibility.Visible;
        Stage.Visibility = Visibility.Collapsed;
        SyncTitleBarBackdrop();
    }

    private void CloseOverlays() => SettingsOverlay.Visibility = Visibility.Collapsed;

    private void CloseOverlays_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlays();
        Stage.Visibility = Visibility.Visible;
        SyncTitleBarBackdrop();
    }

    /// <summary>Match the title bar to the ambient page, or to the opaque overlay when one is open.</summary>
    private void SyncTitleBarBackdrop()
    {
        var overlayOpen = SettingsOverlay.Visibility == Visibility.Visible;
        TitleBarControl.Background = overlayOpen
            ? (TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Transparent)
            : Brushes.Transparent;
    }

    private void LibraryTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string view) return;
        _view = view;
        _onlineMode = false;
        UpdateOnlineButton();
        foreach (var tab in new[] { LibTabSongs, LibTabAlbums, LibTabArtists, LibTabFavorites, LibTabRecent, LibTabPlaylists })
            tab.IsChecked = ReferenceEquals(tab, button);
        RenderLibrary();
    }

    /* ===================== settings overlay ===================== */

    private string _settingsSection = "library";

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string section) return;
        BuildSettings(section);
    }

    private void BuildSettings(string section)
    {
        _settingsSection = section;
        SettingsTitle.Text = section switch
        {
            "appearance" => "外观",
            "playback" => "播放",
            "lyrics" => "歌词",
            "online" => "在线",
            "about" => "关于",
            _ => "音乐库"
        };
        foreach (var pair in new[] { ("library", SetNavLibrary), ("appearance", SetNavAppearance), ("playback", SetNavPlayback), ("lyrics", SetNavLyrics), ("online", SetNavOnline), ("about", SetNavAbout) })
            pair.Item2.Style = (Style)FindResource(pair.Item1 == section ? "NavButtonActive" : "NavButton");

        SettingsBody.Children.Clear();
        switch (section)
        {
            case "appearance": BuildAppearance(); break;
            case "playback": BuildPlayback(); break;
            case "lyrics": BuildLyricsSettings(); break;
            case "online": BuildOnline(); break;
            case "about": BuildAbout(); break;
            default: BuildLibrarySettings(); break;
        }
    }

    private static readonly Dictionary<string, string> FluentBrushAlias = new(StringComparer.Ordinal)
    {
        ["AccentBrush"] = "AccentFillColorDefaultBrush",
        ["SurfaceBrush"] = "CardBackgroundFillColorDefaultBrush",
        ["SunkenBrush"] = "ControlFillColorSecondaryBrush",
        ["TextPrimaryBrush"] = "TextFillColorPrimaryBrush",
        ["TextSecondaryBrush"] = "TextFillColorSecondaryBrush",
        ["TextMutedBrush"] = "TextFillColorTertiaryBrush",
        ["OnArtworkSoftBrush"] = "TextFillColorSecondaryBrush",
        ["BorderBrush"] = "CardStrokeColorDefaultBrush",
    };

    private Brush ResBrush(string key)
        => TryFindResource(FluentBrushAlias.TryGetValue(key, out var fluent) ? fluent : key) as Brush ?? Brushes.Transparent;

    private static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return (Color)ColorConverter.ConvertFromString(text); }
        catch { return null; }
    }

    private static Border Card(string title, UIElement content)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style?)Application.Current.FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(content);
        return new Border
        {
            Style = (Style?)Application.Current.FindResource("SectionCard"),
            Child = panel
        };
    }

    private UIElement Field(string label, string? sub, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = ResBrush("TextPrimaryBrush") });
        if (!string.IsNullOrWhiteSpace(sub))
            text.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = ResBrush("TextMutedBrush"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private ToggleButton Switch(bool value, Action<bool> onChange)
    {
        var toggle = new ToggleButton { Style = (Style)FindResource("SwitchToggle"), IsChecked = value };
        toggle.Click += (_, _) => onChange(toggle.IsChecked == true);
        return toggle;
    }

    private Panel Segment(string[] labels, int selected, Action<int> onSelect, bool wrap = false)
    {
        Panel host = wrap
            ? new WrapPanel { Orientation = Orientation.Horizontal }
            : new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<Button>();
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = labels[i],
                Style = (Style)FindResource(i == selected ? "PrimaryActionButton" : "GhostButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (_, _) =>
            {
                for (var j = 0; j < buttons.Count; j++)
                    buttons[j].Style = (Style)FindResource(j == index ? "PrimaryActionButton" : "GhostButton");
                onSelect(index);
            };
            buttons.Add(button);
            host.Children.Add(button);
        }
        return host;
    }

    private void BuildLibrarySettings()
    {
        var folderList = new StackPanel();
        void RefreshFolders()
        {
            folderList.Children.Clear();
            if (_folders.Count == 0)
                folderList.Children.Add(new TextBlock { Text = "还没有音乐文件夹", Foreground = ResBrush("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
            foreach (var folder in _folders.ToList())
            {
                var path = folder;
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new TextBlock { Text = path, Foreground = ResBrush("TextSecondaryBrush"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var remove = new Button { Content = "移除", Style = (Style)FindResource("GhostButton") };
                remove.Click += (_, _) => { _folders.Remove(path); SaveSettings(); RefreshFolders(); };
                Grid.SetColumn(text, 0); Grid.SetColumn(remove, 1);
                row.Children.Add(text); row.Children.Add(remove);
                folderList.Children.Add(row);
            }
        }
        RefreshFolders();

        var add = new Button { Content = "＋ 添加文件夹", Style = (Style)FindResource("PrimaryActionButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 10, 8, 0) };
        add.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择音乐文件夹" };
            if (picker.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(picker.FolderName)
                && !_folders.Contains(picker.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _folders.Add(picker.FolderName);
                SaveSettings();
                RefreshFolders();
                // 选择完文件夹立即触发扫描，扫描进度显示在“重新扫描”下方
                await ScanAsync();
            }
        };
        var rescan = new Button { Content = "⟳ 重新扫描", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 10, 0, 0) };
        rescan.Click += async (_, _) => await ScanAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(add); buttons.Children.Add(rescan);

        // 扫描进度提示（仅扫描时显示在“重新扫描”下方）
        _settingsScanArea = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) };
        if (_scanning) _settingsScanArea.Visibility = Visibility.Visible;
        _settingsScanStatus = new TextBlock { Text = "正在扫描…", FontSize = 11.5, Foreground = ResBrush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 4) };
        _settingsScanCount = new TextBlock { FontSize = 10.5, Foreground = ResBrush("TextMutedBrush") };
        _settingsScanProgress = new ProgressBar { Height = 4, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 3, 10, 3), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        cancelBtn.Click += ScanCancel_Click;
        _settingsScanArea.Children.Add(_settingsScanStatus);
        _settingsScanArea.Children.Add(_settingsScanCount);
        _settingsScanArea.Children.Add(_settingsScanProgress);
        _settingsScanArea.Children.Add(cancelBtn);

        var stack = new StackPanel();
        stack.Children.Add(folderList);
        stack.Children.Add(buttons);
        stack.Children.Add(_settingsScanArea);

        SettingsBody.Children.Add(Card("音乐文件夹", stack));

        var browse = new Button { Content = "打开音乐库", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(14, 8, 14, 8) };
        browse.Click += (_, _) => MyLibrary_Click(this, new RoutedEventArgs());
        var browsePanel = new StackPanel();
        browsePanel.Children.Add(new TextBlock { Text = "浏览歌曲 / 专辑 / 艺术家 / 收藏 / 最近", Foreground = ResBrush("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        browsePanel.Children.Add(browse);
        SettingsBody.Children.Add(Card("库浏览", browsePanel));
    }

    private void BuildAppearance()
    {
        var gallery = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in ThemeManager.Presets) gallery.Children.Add(BuildPresetTile(preset));
        SettingsBody.Children.Add(Card("主题", gallery));

        var themePanel = new StackPanel();
        var themeIndex = ParseTheme(_themeName) switch
        {
            ThemeManager.ThemeMode.Light => 1,
            ThemeManager.ThemeMode.Dark => 2,
            _ => 0
        };
        themePanel.Children.Add(Field("颜色模式", "主题预设默认值，可在此覆盖", Segment(new[] { "跟随系统", "浅色", "深色" }, themeIndex, index =>
        {
            _themeName = index switch { 1 => "light", 2 => "dark", _ => "system" };
            ThemeManager.SetMode(ParseTheme(_themeName));
            SaveSettings();
        })));
        var accentIndex = _accentSource == "cover" ? 0 : 1;
        themePanel.Children.Add(Field("强调色来源", "跟随封面时按当前专辑封面自动取色", Segment(new[] { "跟随封面", "主题色" }, accentIndex, index =>
        {
            _accentSource = index == 0 ? "cover" : "preset";
            if (_accentSource == "preset")
                ThemeManager.ApplyAccent(ThemeManager.CurrentPreset.Accent);
            else
                ThemeManager.ApplyAccent(_currentIndex >= 0
                    ? (CoverColorExtractor.Extract(ArtworkLoader.LoadForSong(_songs[_currentIndex].Song)) ?? ThemeManager.CurrentPreset.Accent)
                    : ThemeManager.CurrentPreset.Accent);
            UpdateNowPlayingGradient();
            SaveSettings();
        })));
        SettingsBody.Children.Add(Card("颜色", themePanel));

        var motion = new StackPanel();
        motion.Children.Add(Field("精简动效", "减少过渡与动画", Switch(_reduceMotion, value => { _reduceMotion = value; SaveSettings(); UpdateDiscRotation(); })));
        SettingsBody.Children.Add(Card("动效", motion));

        var display = new StackPanel();
        display.Children.Add(Field("界面模式", "普通 → CD 唱片 → 可视化，点击窗口右上角唱片图标切换", Segment(new[] { "普通", "CD 唱片", "可视化" }, _displayMode switch { "cd" => 1, "visualizer" => 2, _ => 0 }, index =>
        {
            _displayMode = index switch { 1 => "cd", 2 => "visualizer", _ => "normal" };
            ApplyDisplayMode();
            SaveSettings();
        })));
        SettingsBody.Children.Add(Card("播放界面", display));

        var disc = new StackPanel();
        disc.Children.Add(Field("唱片旋转", "播放时缓慢旋转光碟", Switch(_discRotation, value => { _discRotation = value; SaveSettings(); UpdateDiscRotation(); })));
        disc.Children.Add(SliderRow("光碟大小", "普通模式下光碟的显示比例（CD 模式请用滚轮缩放）", 0.8, 1.5, 0.05, _discSize,
            v => $"{v * 100:0}%", value => { _discSize = value; SaveSettings(); UpdateDiscRotation(); }));
        disc.Children.Add(SliderRow("CD 缩放", "CD 模式唱片大小（窗口内滚轮 / 双指缩放，100% 填满窗口）", CdZoomMin * 100, CdZoomMax * 100, 5, _cdZoom * 100,
            v => $"{v:0}%", value => { SetCdZoom(value / 100.0); }));
        disc.Children.Add(SliderRow("转动速度", "数值越小转得越快（秒 / 圈）", 4, 60, 1, _discSpeed,
            v => $"{v:0} 秒/圈", value => { _discSpeed = value; SaveSettings(); UpdateDiscRotation(); }));
        SettingsBody.Children.Add(Card("唱片", disc));

        var window = new StackPanel();
        window.Children.Add(SliderRow("窗口透明度", "数值越大越透明（可透出桌面），100% 时完全隐藏，可由托盘恢复", 0.0, 1.0, 0.05, Math.Round(1.0 - _windowOpacity, 2),
            v => $"{v * 100:0}%", value => { ApplyWindowOpacity(1.0 - value); SaveSettings(); }));
        window.Children.Add(Field("隐藏标题栏 LOGO 与标题", "隐藏窗口左上角的图标和「XSTAR」文字", Switch(_hideTitleBrand, value =>
        {
            _hideTitleBrand = value;
            ApplyTitleBrand();
            SaveSettings();
        })));
        SettingsBody.Children.Add(Card("窗口", window));

        // Visualizer settings
        var vizPanel = new StackPanel();
        var renderers = _visualizerEngine.AllRenderers;
        var styleLabels = renderers.Select(r => r.DisplayName).ToArray();
        var styleIds = renderers.Select(r => r.StyleId).ToArray();
        int selectedIdx = Math.Max(0, Array.IndexOf(styleIds, _visualizerStyle));
        vizPanel.Children.Add(Field("可视化风格", "切换不同的音频反应效果", Segment(styleLabels, selectedIdx, index =>
        {
            _visualizerStyle = styleIds[index];
            _visualizerEngine.SetStyle(_visualizerStyle);
            SaveSettings();
        }, wrap: true)));
        vizPanel.Children.Add(Field("显示歌曲信息叠加层", "可视化模式下在底部显示标题/艺术家", Switch(_visualizerShowInfo, value =>
        {
            _visualizerShowInfo = value;
            UpdateVisualizerInfoOverlay();
            SaveSettings();
        })));
        SettingsBody.Children.Add(Card("可视化", vizPanel));
    }

    private FrameworkElement SliderRow(string label, string? sub, double min, double max, double step, double value,
        Func<double, string> format, Action<double> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = ResBrush("TextPrimaryBrush") });
        if (!string.IsNullOrWhiteSpace(sub))
            text.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = ResBrush("TextMutedBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

        var valueText = new TextBlock
        {
            Text = format(value),
            FontSize = 12.5,
            Foreground = ResBrush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0)
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            SmallChange = step,
            LargeChange = step * 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, e) => { valueText.Text = format(e.NewValue); onChange(e.NewValue); };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);

        Grid.SetColumn(text, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        return grid;
    }

    private FrameworkElement BuildPresetTile(ThemeManager.ThemePreset preset)
    {
        var selected = string.Equals(preset.Id, _themePreset, StringComparison.OrdinalIgnoreCase);

        var swatch = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(preset.Accent),
            VerticalAlignment = VerticalAlignment.Center
        };
        var name = new TextBlock { Text = preset.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ResBrush("TextPrimaryBrush") };
        var mode = new TextBlock { Text = preset.Description, FontSize = 11, Foreground = ResBrush("TextMutedBrush"), Margin = new Thickness(0, 2, 0, 0) };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        labels.Children.Add(name);
        labels.Children.Add(mode);
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(swatch);
        stack.Children.Add(labels);

        var tile = new Border
        {
            Width = 168,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            Background = ResBrush(selected ? "SunkenBrush" : "SurfaceBrush"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? new SolidColorBrush(ThemeManager.AccentColor) : ResBrush("BorderBrush"),
            Child = stack
        };
        tile.MouseEnter += (_, _) => { if (preset.Id != _themePreset) tile.Background = ResBrush("SunkenBrush"); };
        tile.MouseLeave += (_, _) => { if (preset.Id != _themePreset) tile.Background = ResBrush("SurfaceBrush"); };
        tile.MouseLeftButtonUp += (_, _) => SelectPreset(preset);
        tile.ToolTip = $"{preset.Name} · {preset.Description}";
        System.Windows.Automation.AutomationProperties.SetName(tile, $"主题 {preset.Name}");
        return tile;
    }

    private void SelectPreset(ThemeManager.ThemePreset preset)
    {
        _themePreset = preset.Id;
        _themeName = preset.Mode switch
        {
            ThemeManager.ThemeMode.Light => "light",
            ThemeManager.ThemeMode.Dark => "dark",
            _ => "system"
        };
        _accentSource = "preset";
        ThemeManager.ApplyPreset(preset);
        UpdateNowPlayingGradient();
        SaveSettings();
        BuildSettings("appearance");
    }

    private string _accentSource = "cover";

    private void BuildPlayback()
    {
        var panel = new StackPanel();
        panel.Children.Add(Field("默认播放模式", "顺序 / 随机 / 单曲循环", new TextBlock { Text = _playbackMode switch { PlaybackMode.Shuffle => "随机播放", PlaybackMode.RepeatOne => "单曲循环", _ => "顺序播放" }, Foreground = ResBrush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }));
        panel.Children.Add(Field("启动时恢复上次现场", "队列、进度与播放状态", Switch(true, _ => { })));
        panel.Children.Add(Field("淡入淡出", "切歌与暂停时平滑过渡", Switch(true, _ => { })));
        SettingsBody.Children.Add(Card("播放", panel));

        SettingsBody.Children.Add(Card("均衡器", BuildEqPanel()));
    }

    private void BuildLyricsSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(Field("在线歌词", "本地缺失时从 LRCLIB 获取并缓存", Switch(true, _ => { })));
        var sizes = new[] { 12d, 14d, 17d, 20d };
        var index = Array.IndexOf(sizes, _lyricsFontSize);
        panel.Children.Add(Field("歌词字号", "歌词页面与 CD 歌词", Segment(new[] { "小", "标准", "大", "特大" }, index < 0 ? 1 : index, i => { _lyricsFontSize = sizes[i]; RenderLyrics(); SaveSettings(); })));
        panel.Children.Add(FontFamilyField("歌词字体", "默认使用应用字体", _lyricFontFamily, value => { _lyricFontFamily = value; RenderLyrics(); SaveSettings(); }));

        var activeColor = _lyricActiveColor ?? (ResBrush("AccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x6E, 0x8B, 0xFF);
        panel.Children.Add(ColorField("正在播放颜色", "当前高亮歌词行的前景色", activeColor, color => { _lyricActiveColor = color; UpdateLyricHighlight(force: true); SaveSettings(); }));
        var inactiveColor = _lyricInactiveColor ?? (ResBrush("TextMutedBrush") as SolidColorBrush)?.Color ?? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);
        panel.Children.Add(ColorField("未播放颜色", "其余歌词行的颜色", inactiveColor, color => { _lyricInactiveColor = color; UpdateLyricHighlight(force: true); SaveSettings(); }));

        var clear = new Button { Content = "清理", Style = (Style)FindResource("GhostButton") };
        clear.Click += (_, _) => Toast("歌词缓存已清理");
        panel.Children.Add(Field("清理歌词缓存", "删除已缓存的在线歌词", clear));
        SettingsBody.Children.Add(Card("歌词", panel));
    }

    private UIElement FontFamilyField(string label, string? sub, string current, Action<string> onChange)
    {
        var options = new (string Label, string Value)[]
        {
            ("默认", ""),
            ("微软雅黑", "Microsoft YaHei UI"),
            ("等线", "DengXian"),
            ("宋体", "SimSun"),
            ("楷体", "KaiTi"),
            ("黑体", "SimHei"),
            ("Segoe UI Variable", "Segoe UI Variable Text"),
        };
        var combo = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
        foreach (var option in options) combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        var selected = Array.FindIndex(options, o => string.Equals(o.Value, current ?? "", StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = selected < 0 ? 0 : selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value) onChange(value);
        };
        return Field(label, sub, combo);
    }

    private UIElement ColorField(string label, string? sub, Color value, Action<Color> onChange)
    {
        var palette = new[]
        {
            Colors.White,
            Color.FromRgb(0xC8, 0xC8, 0xC8),
            Color.FromRgb(0x8A, 0x8A, 0x8A),
            Colors.Black,
            Color.FromRgb(0x6E, 0x8B, 0xFF),
            Color.FromRgb(0xB1, 0x5C, 0xFF),
            Color.FromRgb(0x2E, 0xD3, 0xB7),
            Color.FromRgb(0x3D, 0xD6, 0x8C),
            Color.FromRgb(0xFF, 0xD1, 0x66),
            Color.FromRgb(0xFF, 0x7A, 0x59),
            Color.FromRgb(0xF0, 0x6A, 0x9A)
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var hexBox = new TextBox { Text = ToHex(value), Width = 100, Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center };
        var updating = false;

        foreach (var color in palette)
        {
            var brush = new SolidColorBrush(color);
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 6, 0),
                Background = brush,
                BorderBrush = ResBrush("BorderBrush"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = ToHex(color)
            };
            var picked = color;
            swatch.MouseLeftButtonUp += (_, _) => { updating = true; hexBox.Text = ToHex(picked); updating = false; onChange(picked); };
            swatches.Children.Add(swatch);
        }

        hexBox.TextChanged += (_, _) =>
        {
            if (updating) return;
            if (ParseColor(hexBox.Text.Trim()) is { } parsed) onChange(parsed);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(swatches);
        row.Children.Add(hexBox);
        return Field(label, sub, row);
    }

    private static string ToHex(Color color)
        => color.A == 0xFF
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static readonly (string Label, string Value)[] GdSources =
    {
        ("网易云", "netease"), ("QQ音乐", "tencent"), ("酷狗", "kugou"), ("酷我", "kuwo"),
        ("咪咕", "migu"), ("Tidal", "tidal"), ("Qobuz", "qobuz"), ("Spotify", "spotify"),
        ("YouTube Music", "ytmusic"), ("Apple Music", "apple")
    };

    private static readonly (string Label, int Value)[] GdBitrates =
    {
        ("128K", 128), ("192K", 192), ("320K", 320), ("无损", 740), ("Hi-Res", 999)
    };

    private void BuildOnline()
    {
        var panel = new StackPanel();

        var providerCombo = new ComboBox { Width = 180, VerticalContentAlignment = VerticalAlignment.Center };
        providerCombo.Items.Add(new ComboBoxItem { Content = "全部音源", Tag = OnlineMusicService.AllProviders });
        foreach (var provider in _online.Providers)
            providerCombo.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.Key });
        foreach (var item in providerCombo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, _onlineProvider, StringComparison.OrdinalIgnoreCase)) providerCombo.SelectedItem = item;
        if (providerCombo.SelectedItem is null && providerCombo.Items.Count > 0) providerCombo.SelectedIndex = 0;
        providerCombo.SelectionChanged += (_, _) =>
        {
            if (providerCombo.SelectedItem is not ComboBoxItem chosen) return;
            _onlineProvider = chosen.Tag as string ?? OnlineMusicService.AllProviders;
            SelectOnlineSource(_onlineProvider);
            SaveSettings();
        };
        panel.Children.Add(Field("默认音源", "在线搜索使用的音源，「全部」会合并多源结果", providerCombo));

        var gdSourceCombo = new ComboBox { Width = 160, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in GdSources)
            gdSourceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        foreach (var item in gdSourceCombo.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, _gdSource, StringComparison.OrdinalIgnoreCase)) gdSourceCombo.SelectedItem = item;
        if (gdSourceCombo.SelectedItem is null && gdSourceCombo.Items.Count > 0) gdSourceCombo.SelectedIndex = 0;
        gdSourceCombo.SelectionChanged += (_, _) =>
        {
            if (gdSourceCombo.SelectedItem is not ComboBoxItem chosen) return;
            _gdSource = chosen.Tag as string ?? "netease";
            ConfigureOnlineProviders();
            SaveSettings();
        };
        panel.Children.Add(Field("GD · 音乐源", "GD音乐台聚合的底层曲库", gdSourceCombo));

        var gdBitrateCombo = new ComboBox { Width = 160, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in GdBitrates)
            gdBitrateCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value.ToString() });
        foreach (var item in gdBitrateCombo.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == _gdBitrate.ToString()) gdBitrateCombo.SelectedItem = item;
        if (gdBitrateCombo.SelectedItem is null && gdBitrateCombo.Items.Count > 0) gdBitrateCombo.SelectedIndex = 2;
        gdBitrateCombo.SelectionChanged += (_, _) =>
        {
            if (gdBitrateCombo.SelectedItem is not ComboBoxItem chosen) return;
            if (int.TryParse(chosen.Tag as string, out var bitrate)) { _gdBitrate = bitrate; ConfigureOnlineProviders(); SaveSettings(); }
        };
        panel.Children.Add(Field("GD · 音质", "320K 以上为无损 / Hi-Res", gdBitrateCombo));

        var audiusBox = new TextBox { Text = _audiusAppName, Padding = new Thickness(10, 7, 10, 7), Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
        var audiusHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = audiusBox };
        audiusBox.TextChanged += (_, _) => { _audiusAppName = audiusBox.Text.Trim(); ConfigureOnlineProviders(); SaveSettings(); };
        panel.Children.Add(Field("Audius app_name", "向 Audius 标识应用，可留默认", audiusHost));

        var clientBox = new TextBox { Text = _jamendoClientId, Padding = new Thickness(10, 7, 10, 7), Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
        var clientHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = clientBox };
        clientBox.TextChanged += (_, _) => { _jamendoClientId = clientBox.Text.Trim(); ConfigureOnlineProviders(); SaveSettings(); };
        panel.Children.Add(Field("Jamendo client_id", "在 developer.jamendo.com 免费申请（可选）", clientHost));

        var downloadBox = new TextBox { Text = _downloadFolder, Padding = new Thickness(10, 7, 10, 7), Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
        var downloadHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = downloadBox };
        var browse = new Button { Content = "选择…", Style = (Style)FindResource("GhostButton") };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择下载目录" };
            if (picker.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(picker.FolderName))
            {
                downloadBox.Text = picker.FolderName;
                RegisterDownloadFolder(picker.FolderName, scan: true);
            }
        };
        downloadBox.TextChanged += (_, _) => { _downloadFolder = downloadBox.Text.Trim(); SaveSettings(); };
        downloadBox.LostFocus += (_, _) => RegisterDownloadFolder(_downloadFolder);
        var downloadRow = new StackPanel { Orientation = Orientation.Horizontal };
        downloadRow.Children.Add(downloadHost); downloadRow.Children.Add(browse);
        panel.Children.Add(Field("下载目录", "在线下载保存位置（自动加入扫描文件夹）", downloadRow));
        SettingsBody.Children.Add(Card("在线", panel));
    }

    private void BuildAbout()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "XSTAR Music for Windows", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ResBrush("TextPrimaryBrush") });
        panel.Children.Add(new TextBlock
        {
            Text = "本地优先的 Windows 桌面音乐播放器\n播放引擎 BASS / 元数据 TagLib# / 数据库 SQLite",
            Foreground = ResBrush("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = $"当前播放后端：{_player.Name}", Foreground = ResBrush("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
        SettingsBody.Children.Add(Card("关于", panel));
    }

    private static ThemeManager.ThemeMode ParseTheme(string value) => value switch
    {
        "light" => ThemeManager.ThemeMode.Light,
        "dark" => ThemeManager.ThemeMode.Dark,
        _ => ThemeManager.ThemeMode.System
    };

    /* ===================== responsive sizing ===================== */

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = Stage.ActualWidth;
        var height = Stage.ActualHeight;
        var glow = Math.Max(400, Math.Min(1100, Math.Min(width, height) * 1.6));
        NpBackground.Width = glow;
        NpBackground.Height = glow;
        PlaybackBar.Width = Math.Max(300, Math.Min(680, width - 24));
        UpdateDiscRotation();
    }

    /* ===================== watchers ===================== */

    private void ConfigureWatchers()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        foreach (var folder in _folders.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Created += OnWatchedChange;
            watcher.Changed += OnWatchedChange;
            watcher.Deleted += OnWatchedChange;
            watcher.Renamed += OnWatchedRename;
            _watchers[folder] = watcher;
        }
    }

    private void OnWatchedChange(object sender, FileSystemEventArgs e) => RequestRescan();
    private void OnWatchedRename(object sender, RenamedEventArgs e) => RequestRescan();

    private void RequestRescan()
    {
        _scanDebounce?.Cancel();
        var tokenSource = new CancellationTokenSource();
        _scanDebounce = tokenSource;
        _ = DebouncedRescanAsync(tokenSource.Token);
    }

    private async Task DebouncedRescanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                await Dispatcher.InvokeAsync(ScanAsync);
        }
        catch (OperationCanceledException) { }
    }

    /* ===================== input ===================== */

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { Play_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Left) Previous_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.Right) Next_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.Escape)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible) CloseOverlays_Click(sender, new RoutedEventArgs());
            else if (_page is not null) { _page = null; ApplyPage(); }
        }
    }

    private static IPlayerEngine CreateEngine()
    {
        try { return new BassPlayerEngine(); }
        catch { return new MediaPlayerEngine(); }
    }

    private static void Toast(string message) => MessageBox.Show(message, "XSTAR", MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed class AppState
    {
        public List<string> Folders { get; set; } = new();
        public string? LastPath { get; set; }
        public PlaybackMode PlaybackMode { get; set; }
        public List<string> QueuePaths { get; set; } = new();
        public long PositionMs { get; set; }
        public double LyricsFontSize { get; set; } = 14;
        public bool DimLyrics { get; set; } = true;
        public bool CdLyrics { get; set; }
        public string LyricFontFamily { get; set; } = "";
        public string? LyricActiveColor { get; set; }
        public string? LyricInactiveColor { get; set; }
        public bool DiscRotation { get; set; } = true;
        public string DisplayMode { get; set; } = "normal";
        public double DiscSize { get; set; } = 1.2;
        public double DiscSpeed { get; set; } = 26;
        public double CdZoom { get; set; } = 1.0;
        public double WindowOpacity { get; set; } = 1.0;
        public bool HideTitleBrand { get; set; }
        public bool ReduceMotion { get; set; }
        public string Theme { get; set; } = "system";
        public string ThemePreset { get; set; } = "deepspace";
        public string AccentSource { get; set; } = "cover";
        public string JamendoClientId { get; set; } = "";
        public string DownloadFolder { get; set; } = "";
        public string OnlineProvider { get; set; } = OnlineMusicService.AllProviders;
        public string GdSource { get; set; } = "netease";
        public int GdBitrate { get; set; } = 320;
        public string AudiusAppName { get; set; } = "XstarMusic";
        public bool EqEnabled { get; set; }
        public List<double>? EqGains { get; set; }
        public string VisualizerStyle { get; set; } = "stardust";
        public bool VisualizerShowInfo { get; set; } = true;
        public bool VisualizerLyrics { get; set; }
        public string ThreeDQuality { get; set; } = "balanced";
        public string ThreeDShelfMode { get; set; } = "side";
        public string ThreeDShelfPresence { get; set; } = "always";
        public string ThreeDCameraMode { get; set; } = "dynamic";
        public string ThreeDLibraryKind { get; set; } = "albums";
        public int ThreeDCoverPreset { get; set; }
        public bool ThreeDBloom { get; set; } = true;
        public bool ThreeDLyrics { get; set; } = true;
        public double ThreeDShelfSize { get; set; } = 1.0;
        public double ThreeDShelfOffsetX { get; set; }
        public double ThreeDShelfOffsetY { get; set; }
        public double ThreeDShelfAngleY { get; set; }
        public double ThreeDShelfOpacity { get; set; } = 1.0;
        public string ThreeDBackgroundColor { get; set; } = "#05060A";
        public string ThreeDBackgroundImage { get; set; } = "";
        public double ThreeDParticleSize { get; set; } = 1.0;
        public double ThreeDGlow { get; set; } = 1.0;
        public double ThreeDParticleCount { get; set; } = 1.0;
        public int ThreeDParticleColorMode { get; set; } = 1;
        public string ThreeDParticleColor { get; set; } = "";
    }

    public sealed class SongRow
    {
        public SongRow(Song song, int index) { Song = song; Index = index; DisplayIndex = index.ToString("00"); }
        public Song Song { get; }
        public int Index { get; }
        public string DisplayIndex { get; }
        public string Title => Song.Title;
        public string Artist => Song.Artist;
        public string Format => Song.Format;
        public string DurationText => Song.DurationMs > 0 ? TimeSpan.FromMilliseconds(Song.DurationMs).ToString(@"mm\:ss") : "--:--";
        public bool IsFavorite { get; set; }
        public string FavoriteGlyph => IsFavorite ? "\uEB52" : "\uEB51";
        public OnlineTrack? Online { get; set; }
        public bool IsStatus { get; set; }
    }

    public sealed class LibraryItem
    {
        public LibraryItem(string name, string subtitle, string? artworkPath, int count)
        {
            Name = name; Subtitle = subtitle; ArtworkPath = artworkPath; Count = count;
        }
        public string Name { get; }
        public string Subtitle { get; }
        public string? ArtworkPath { get; }
        public int Count { get; }
    }
}
