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
using Xstar.Music.Core;

namespace Xstar.Music.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
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
    private double _windowOpacity = 1.0;
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
    private readonly List<SongRow> _onlineResults = new();
    private CancellationTokenSource? _onlineSearch;
    private bool _onlineMode;
    private string _jamendoClientId = "";
    private string _downloadFolder = "";

    private int _sortMode;

    // EQ state
    private bool _eqEnabled;
    private readonly double[] _eqGains = new double[10];
    private readonly List<Slider> _eqSliders = new();
    private readonly List<TextBlock> _eqGainTexts = new();
    private bool _suppressEqSave;

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

    public MainWindow()
    {
        InitializeComponent();
        SongList.ItemsSource = _songs;

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
        UpdateFrameClip();
    }

    private void OnThemeChanged()
    {
        UpdateNowPlayingGradient();
        UpdateDiscLabel(_discArtwork, ThemeManager.AccentColor);
        UpdateLyricHighlight(force: true);
        // ApplicationThemeManager repaints the window background right after this, so re-assert
        // the fully transparent window surface (window background + composition target).
        EnsureTransparentWindowBackground();
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
        var radius = WindowState == WindowState.Maximized || string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase) ? 0 : FrameCornerRadius;
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

    private void PageToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = _page != "playlist";
        if (open) CloseOverlays();
        _page = open ? "playlist" : null;
        Stage.Visibility = Visibility.Visible;
        ApplyPage();
        SyncTitleBarBackdrop();
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        var open = _page != "lyrics";
        if (open) CloseOverlays();
        _page = open ? "lyrics" : null;
        Stage.Visibility = Visibility.Visible;
        ApplyPage();
        SyncTitleBarBackdrop();
    }

    private void ApplyPage()
    {
        var album = _page is null;
        AlbumView.Visibility = album ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPage.Visibility = _page == "playlist" ? Visibility.Visible : Visibility.Collapsed;
        LyricsPage.Visibility = _page == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        if (_page == "lyrics") RenderLyrics();
        else if (_page == "playlist") RenderQueue();
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
                _windowOpacity = state?.WindowOpacity is >= 0 and <= 1 ? state.WindowOpacity : 1.0;
                _reduceMotion = state?.ReduceMotion ?? false;
                _themeName = state?.Theme ?? "system";
                _themePreset = state?.ThemePreset ?? "deepspace";
                _accentSource = state?.AccentSource ?? "cover";
                _jamendoClientId = state?.JamendoClientId ?? "";
                _downloadFolder = state?.DownloadFolder ?? "";
                _jamendo.ClientId = _jamendoClientId;
                _eqEnabled = state?.EqEnabled ?? false;
                var gains = state?.EqGains;
                if (gains is not null && gains.Count == _eqGains.Length)
                    for (var i = 0; i < _eqGains.Length; i++) _eqGains[i] = gains[i];
            }
            ThemeManager.ApplyPreset(ThemeManager.GetPreset(_themePreset));
            ThemeManager.SetMode(ParseTheme(_themeName));
            ApplyWindowOpacity(_windowOpacity);
            EnsureTransparentWindowBackground();
            ModeGlyphText.Text = ModeGlyph(_playbackMode);
            _player.SetMode(_playbackMode);
            UpdateOnlineButton();
            if (_folders.Count > 0) await ScanAsync();
            else RenderQueue();
            UpdateDiscRotation();
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
                WindowOpacity = _windowOpacity,
                ReduceMotion = _reduceMotion,
                Theme = _themeName,
                ThemePreset = _themePreset,
                AccentSource = _accentSource,
                JamendoClientId = _jamendoClientId,
                DownloadFolder = _downloadFolder,
                EqEnabled = _eqEnabled,
                EqGains = _eqGains.ToList()
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
        SongList.ItemsSource = _songs.ToList();
    }

    private void RenderLibrary()
    {
        if (_onlineMode)
        {
            var onlineKeyword = SearchBox.Text.Trim();
            var online = _onlineResults.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(onlineKeyword))
                online = online.Where(x => $"{x.Title}{x.Artist}{x.Song.Album}".Contains(onlineKeyword, StringComparison.OrdinalIgnoreCase));
            LibraryList.ItemsSource = online.ToList();
            LibraryList.Visibility = Visibility.Visible;
            LibraryGridScroll.Visibility = Visibility.Collapsed;
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
            LibraryGridScroll.Visibility = Visibility.Visible;
            LibraryList.Visibility = Visibility.Collapsed;
            return;
        }

        if (_view is "albums" or "artists")
        {
            var cards = _view == "albums"
                ? rows.GroupBy(x => x.Song.Album).Select(g => new LibraryItem(g.Key, g.First().Song.Artist, g.First().Song.ArtworkPath, g.Count())).ToList()
                : rows.GroupBy(x => x.Song.Artist).Select(g => new LibraryItem(g.Key, $"{g.Count()} 首", g.First().Song.ArtworkPath, g.Count())).ToList();
            LibraryGrid.ItemsSource = cards;
            LibraryGridScroll.Visibility = Visibility.Visible;
            LibraryList.Visibility = Visibility.Collapsed;
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
        LibraryList.Visibility = Visibility.Visible;
        LibraryGridScroll.Visibility = Visibility.Collapsed;
    }

    /* ===================== selection / playback ===================== */

    private void SelectSong(int index, bool play)
    {
        if (index < 0 || index >= _songs.Count) return;
        _currentIndex = index;
        var song = _songs[index].Song;
        if (play)
        {
            var playedAt = DateTime.UtcNow.Ticks;
            _database.AddHistory(song.Id, playedAt);
            _historyTicks[song.Id] = playedAt;
        }

        TitleText.Text = song.Title;
        SubText.Text = string.IsNullOrWhiteSpace(song.Album) ? song.Artist : $"{song.Artist} · {song.Album}";
        CdTitleText.Text = song.Title;
        CdSubText.Text = song.Artist;
        var artwork = ArtworkLoader.LoadForSong(song);
        QualityText.Text = song.Quality;
        ApplyQualityVisual(song.Quality);
        var spec = BuildMetaText(song);
        SpecChip.Visibility = string.IsNullOrWhiteSpace(spec) ? Visibility.Collapsed : Visibility.Visible;
        SpecText.Text = spec ?? "";

        var accent = _accentSource == "cover"
            ? (CoverColorExtractor.Extract(artwork) ?? ThemeManager.CurrentPreset.Accent)
            : ThemeManager.CurrentPreset.Accent;
        ThemeManager.ApplyAccent(accent);
        UpdateNowPlayingGradient();
        _discArtwork = artwork;
        UpdateDiscLabel(artwork, accent);

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
        if (cd)
        {
            var side = Math.Min(Stage.ActualWidth, Stage.ActualHeight);
            if (side > 40) scale = side / 340.0;
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

    private void ApplyDisplayMode()
    {
        var cd = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase);

        TitleBarControl.Opacity = cd ? 0 : 1;
        TitleBarControl.IsHitTestVisible = !cd;
        TitleBarControl.Height = cd ? 0 : 32;
        NpBackground.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        PlaybackBar.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        InfoChips.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        PlaylistButton.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        LyricsButton.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        LibraryButton.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        EqButton.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingDisc.Margin = cd ? new Thickness(0) : new Thickness(0, 0, 0, 72);
        NowPlayingInfo.Margin = cd ? new Thickness(24) : new Thickness(24, 0, 24, 72);
        DiscBase.Effect = cd ? null : _discShadow;

        if (cd)
        {
            WindowFrame.Background = Brushes.Transparent;
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

        if (cd)
        {
            _page = null;
            ApplyPage();
            CloseOverlays();
            Stage.Visibility = Visibility.Visible;
            SyncTitleBarBackdrop();
        }

        ApplyCdWindow(cd);
        ApplyCdChrome(cd);
        ApplyCdLyrics();
        UpdateFrameClip();
        UpdateDiscRotation();
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
    private double _preCdMinWidth = 680;
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
        if (!_stageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _stageDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _stageDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _stageDragArmed = false;
        _suppressDiscClick = true;
        try { DragMove(); } catch { /* 非左键拖动时忽略 */ }
    }

    private void Stage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _stageDragArmed = false;

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
        _displayMode = string.Equals(_displayMode, "cd", StringComparison.OrdinalIgnoreCase) ? "normal" : "cd";
        ApplyDisplayMode();
        SaveSettings();
    }

    private void ExitCdMode_Click(object sender, RoutedEventArgs e)
    {
        _displayMode = "normal";
        ApplyDisplayMode();
        SaveSettings();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 && _songs.Count > 0) { SelectSong(0, true); return; }
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
        if (row.Online is not null) { _ = DownloadAndPlayAsync(row); return; }
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
    }

    /* ===================== volume / progress ===================== */

    private void Volume_Click(object sender, RoutedEventArgs e) => VolumePopup.IsOpen = true;

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player.SetVolume(e.NewValue);
        if (VolumeText is not null) VolumeText.Text = $"{e.NewValue:P0}";
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
        LyricsHead.Text = $"{song.Title} · {song.Artist}";
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
        _lyricBlocks.Clear();
        _cdLyricBlocks.Clear();
        _lastLyricIndex = -1;
    }

    private void BuildLyricBlocks()
    {
        foreach (var line in _lyricLines)
        {
            _lyricBlocks.Add(CreateLyricBlock(line, LyricsPanel));
            _cdLyricBlocks.Add(CreateLyricBlock(line, CdLyricsPanel));
        }
    }

    private TextBlock CreateLyricBlock(LyricLine line, Panel host)
    {
        var family = string.IsNullOrWhiteSpace(_lyricFontFamily)
            ? (Application.Current?.TryFindResource("AppFont") as FontFamily ?? SystemFonts.MessageFontFamily)
            : new FontFamily(_lyricFontFamily);
        var block = new TextBlock
        {
            Text = line.Text,
            Foreground = ResBrush("TextMutedBrush"),
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
        for (var i = 0; i < _lyricBlocks.Count; i++)
        {
            var active = i == current;
            StyleLyricBlock(_lyricBlocks[i], active, activeBrush, inactiveBrush);
            if (i < _cdLyricBlocks.Count) StyleLyricBlock(_cdLyricBlocks[i], active, activeBrush, inactiveBrush);
        }

        var changed = force || current != _lastLyricIndex;
        _lastLyricIndex = current;
        if (!changed) return;
        if (current < _lyricBlocks.Count && LyricsPage.Visibility == Visibility.Visible)
            CenterActiveLyric(LyricsView, _lyricBlocks[current]);
        if (current < _cdLyricBlocks.Count && CdLyricsOverlay.Visibility == Visibility.Visible)
            CenterActiveLyric(CdLyricsView, _cdLyricBlocks[current]);
    }

    /* ===================== equalizer ===================== */

    private void ApplyEqToEngine()
    {
        _player.SetEqualizerEnabled(_eqEnabled);
        for (var i = 0; i < _eqGains.Length; i++)
            _player.SetEqualizerBand(i, _eqGains[i]);
    }

    private static readonly string[] EqBandFrequencies = { "80", "125", "250", "500", "1k", "2k", "4k", "8k", "12k", "16k" };

    private void Eq_Click(object sender, RoutedEventArgs e)
    {
        EqPopup.IsOpen = true;
        if (EqBandsPanel.Items.Count == 0) BuildEqPanel();
        EqEnableToggle.IsChecked = _eqEnabled;
        EqEnableToggle.Content = _eqEnabled ? "开启" : "关闭";
    }

    private void EqEnable_Click(object sender, RoutedEventArgs e)
    {
        _eqEnabled = EqEnableToggle.IsChecked == true;
        EqEnableToggle.Content = _eqEnabled ? "开启" : "关闭";
        ApplyEqToEngine();
        SaveSettings();
        UpdateEqBandSlidersEnabled();
    }

    private void UpdateEqBandSlidersEnabled()
    {
        foreach (var slider in _eqSliders) slider.IsEnabled = _eqEnabled;
    }

    private void BuildEqPanel()
    {
        EqBandsPanel.Items.Clear();
        _eqSliders.Clear();
        _eqGainTexts.Clear();
        for (var band = 0; band < _eqGains.Length; band++)
        {
            var gain = _eqGains[band];
            var column = new StackPanel { Orientation = Orientation.Vertical, Width = 28, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
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
            EqBandsPanel.Items.Add(column);
        }

        // Preset buttons
        EqPresetsPanel.Children.Clear();
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
            EqPresetsPanel.Children.Add(btn);
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
            EqEnableToggle.IsChecked = true;
            EqEnableToggle.Content = "开启";
            UpdateEqBandSlidersEnabled();
            for (var i = 0; i < _eqGains.Length && i < _eqSliders.Count; i++)
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
        if (_onlineMode) QueueOnlineSearch();
        else RenderLibrary();
    }

    private void Online_Click(object sender, RoutedEventArgs e)
    {
        _onlineMode = !_onlineMode;
        UpdateOnlineButton();
        if (_onlineMode) QueueOnlineSearch();
        else { _onlineResults.Clear(); RenderLibrary(); }
    }

    private void UpdateOnlineButton()
    {
        OnlineButton.IsChecked = _onlineMode;
    }

    private void QueueOnlineSearch()
    {
        _onlineSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _onlineSearch = cts;
        _ = OnlineSearchAsync(cts.Token);
    }

    private async Task OnlineSearchAsync(CancellationToken cancellationToken)
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_jamendoClientId))
        {
            _onlineResults.Clear();
            _onlineResults.Add(new SongRow(new Song { Title = "未配置在线音源", Artist = "请在设置中填写 Jamendo client_id" }, 1));
            RenderLibrary();
            return;
        }
        if (query.Length < 2)
        {
            _onlineResults.Clear();
            RenderLibrary();
            return;
        }
        try
        {
            await Task.Delay(450, cancellationToken);
            var tracks = await _jamendo.SearchAsync(query, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            _onlineResults.Clear();
            var index = 1;
            foreach (var track in tracks)
            {
                var song = new Song { Id = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album, DurationMs = track.DurationMs, Format = "MP3", Quality = track.Downloadable ? "在线·可下载" : "在线·试听" };
                _onlineResults.Add(new SongRow(song, index++) { Online = track });
            }
            if (_onlineResults.Count == 0) _onlineResults.Add(new SongRow(new Song { Title = "无搜索结果", Artist = query }, 1));
            RenderLibrary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Toast("在线搜索失败：" + ex.Message); }
    }

    private async Task DownloadAndPlayAsync(SongRow row)
    {
        var track = row.Online!;
        if (string.IsNullOrWhiteSpace(_jamendoClientId)) { Toast("请先在设置中填写 Jamendo client_id"); return; }
        var folder = string.IsNullOrWhiteSpace(_downloadFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "XSTAR") : _downloadFolder;
        try
        {
            Toast($"开始下载：{track.Artist} - {track.Title}");
            var path = await _jamendo.DownloadAsync(track, folder);
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) { _folders.Add(folder); SaveSettings(); }
            _onlineMode = false;
            UpdateOnlineButton();
            await ScanAsync();
            var index = _songs.ToList().FindIndex(r => r.Song.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) SelectSong(index, true);
            Toast("下载完成：" + Path.GetFileName(path));
        }
        catch (Exception ex) { Toast("下载失败：" + ex.Message); }
    }

    /* ===================== overlays ===================== */

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        ShowOverlay(library: true);
        RenderLibrary();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowOverlay(library: false);
        BuildSettings(_settingsSection);
    }

    /// <summary>Opens one top-level overlay and closes every other panel (page + overlays).</summary>
    private void ShowOverlay(bool library)
    {
        _page = null;
        ApplyPage();
        LibraryOverlay.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        Stage.Visibility = Visibility.Collapsed;
        SyncTitleBarBackdrop();
    }

    private void CloseOverlays()
    {
        LibraryOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseOverlays_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlays();
        Stage.Visibility = Visibility.Visible;
        SyncTitleBarBackdrop();
    }

    /// <summary>Match the title bar to the ambient page, or to the opaque overlay when one is open.</summary>
    private void SyncTitleBarBackdrop()
    {
        var overlayOpen = LibraryOverlay.Visibility == Visibility.Visible || SettingsOverlay.Visibility == Visibility.Visible;
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

    private StackPanel Segment(string[] labels, int selected, Action<int> onSelect)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal };
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
        browse.Click += (_, _) => Library_Click(this, new RoutedEventArgs());
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
        display.Children.Add(Field("界面模式", "普通保留完整控制；CD 唱片仅显示旋转唱片，按左 / 中 / 右分区切歌", Segment(new[] { "普通", "CD 唱片" }, _displayMode == "cd" ? 1 : 0, index =>
        {
            _displayMode = index == 1 ? "cd" : "normal";
            ApplyDisplayMode();
            SaveSettings();
        })));
        SettingsBody.Children.Add(Card("播放界面", display));

        var disc = new StackPanel();
        disc.Children.Add(Field("唱片旋转", "播放时缓慢旋转光碟", Switch(_discRotation, value => { _discRotation = value; SaveSettings(); UpdateDiscRotation(); })));
        disc.Children.Add(SliderRow("光碟大小", "播放页中央光碟的显示比例", 0.8, 1.5, 0.05, _discSize,
            v => $"{v * 100:0}%", value => { _discSize = value; SaveSettings(); UpdateDiscRotation(); }));
        disc.Children.Add(SliderRow("转动速度", "数值越小转得越快（秒 / 圈）", 4, 60, 1, _discSpeed,
            v => $"{v:0} 秒/圈", value => { _discSpeed = value; SaveSettings(); UpdateDiscRotation(); }));
        SettingsBody.Children.Add(Card("唱片", disc));

        var window = new StackPanel();
        window.Children.Add(SliderRow("窗口透明度", "数值越大越透明（可透出桌面），100% 时完全隐藏，可由托盘恢复", 0.0, 1.0, 0.05, Math.Round(1.0 - _windowOpacity, 2),
            v => $"{v * 100:0}%", value => { ApplyWindowOpacity(1.0 - value); SaveSettings(); }));
        SettingsBody.Children.Add(Card("窗口", window));
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

    private void BuildOnline()
    {
        var panel = new StackPanel();
        panel.Children.Add(Field("在线搜索", "Jamendo 免费音乐（需要 client_id）", Switch(!string.IsNullOrWhiteSpace(_jamendoClientId), _ => { })));
        var clientBox = new TextBox { Text = _jamendoClientId, Padding = new Thickness(10, 7, 10, 7), Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
        var clientHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = clientBox };
        clientBox.TextChanged += (_, _) => { _jamendoClientId = clientBox.Text.Trim(); _jamendo.ClientId = _jamendoClientId; SaveSettings(); };
        panel.Children.Add(Field("Jamendo client_id", "在 developer.jamendo.com 免费申请", clientHost));
        var downloadBox = new TextBox { Text = _downloadFolder, Padding = new Thickness(10, 7, 10, 7), Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
        var downloadHost = new Border { Background = ResBrush("SunkenBrush"), CornerRadius = new CornerRadius(8), Child = downloadBox };
        var browse = new Button { Content = "选择…", Style = (Style)FindResource("GhostButton") };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择下载目录" };
            if (picker.ShowDialog(this) == true) downloadBox.Text = picker.FolderName;
        };
        downloadBox.TextChanged += (_, _) => { _downloadFolder = downloadBox.Text.Trim(); SaveSettings(); };
        var downloadRow = new StackPanel { Orientation = Orientation.Horizontal };
        downloadRow.Children.Add(downloadHost); downloadRow.Children.Add(browse);
        panel.Children.Add(Field("下载目录", "在线下载保存位置", downloadRow));
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
        PlaybackBar.Width = Math.Max(320, Math.Min(680, width - 40));
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
            if (SettingsOverlay.Visibility == Visibility.Visible || LibraryOverlay.Visibility == Visibility.Visible) CloseOverlays_Click(sender, new RoutedEventArgs());
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
        public double WindowOpacity { get; set; } = 1.0;
        public bool ReduceMotion { get; set; }
        public string Theme { get; set; } = "system";
        public string ThemePreset { get; set; } = "deepspace";
        public string AccentSource { get; set; } = "cover";
        public string JamendoClientId { get; set; } = "";
        public string DownloadFolder { get; set; } = "";
        public bool EqEnabled { get; set; }
        public List<double>? EqGains { get; set; }
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
