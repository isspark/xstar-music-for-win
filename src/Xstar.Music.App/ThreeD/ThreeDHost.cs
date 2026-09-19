using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xstar.Music.Core;

namespace Xstar.Music.App.ThreeD;

/// <summary>Operations the 3D web view can invoke on the host window.</summary>
public interface IThreeDActions
{
    void ThreeDPlay();
    void ThreeDPause();
    void ThreeDNext();
    void ThreeDPrev();
    void ThreeDSeek(long positionMs);
    void ThreeDSetVolume(double volume);
    void ThreeDSetDisplayMode(string mode);
    void ThreeDWindowDrag();
    void ThreeDWindowResize(string edge);
    void ThreeDMinimize();
    void ThreeDMaximize();
    void ThreeDClose();
    void ThreeDPlayTrack(string id);
    string ThreeDGetStateJson();
    string ThreeDGetArtworkJson();
    string ThreeDGetLibraryJson(string kind);
    string ThreeDGetDetailJson(string id);
    string ThreeDGetCardArtworkJson(string key);
    string ThreeDGetLyricsJson();
    string ThreeDGetLyricIndexJson();
    string ThreeDGetSettingsJson();
    void ThreeDPickBackgroundImage();
    void ThreeDClearBackgroundImage();
    void ThreeDOnSetting(string key, JsonElement? value);
}

/// <summary>
/// Owns the WebView2 instance that renders the immersive 3D mode: virtual-host mapping to the
/// bundled web app, the two-way bridge, and the ~30 fps audio/state push loop.
/// </summary>
public sealed class ThreeDHost : IDisposable
{
    private const string VirtualHost = "xstar.local";
    private const string HomeUrl = "https://xstar.local/index.html";

    private readonly WebView2 _view;
    private readonly IPlayerEngine _player;
    private readonly IThreeDActions _actions;
    private readonly AudioFeaturePump _pump;
    private readonly DispatcherTimer _timer;
    private CoreWebView2? _core;
    private Task? _initTask;
    private DateTime _lastTick = DateTime.Now;
    private int _ticksSinceState;

    public ThreeDHost(WebView2 view, IPlayerEngine player, IThreeDActions actions)
    {
        _view = view;
        _player = player;
        _actions = actions;
        _pump = new AudioFeaturePump(player);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    public bool IsReady { get; private set; }
    public bool IsRunning => _timer.IsEnabled;
    public string? LastError { get; private set; }

    /// <summary>Ensures the WebView2 is initialized and starts the push loop. Returns false on failure.</summary>
    public async Task<bool> StartAsync()
    {
        if (!IsReady)
        {
            _initTask ??= InitializeAsync();
            try
            {
                await _initTask;
            }
            catch (Exception ex)
            {
                _initTask = null;
                LastError = ex.Message;
                return false;
            }
        }

        _pump.Reset();
        _lastTick = DateTime.Now;
        _ticksSinceState = 0;
        _timer.Start();
        PushArtwork();
        PushLyrics();
        PushLyricIndex();
        PushSettings();
        PushState();
#if DEBUG
        // Headless verification aid: XSTAR_3D_CAPTURE=<path> dumps the web view after startup.
        var capturePath = Environment.GetEnvironmentVariable("XSTAR_3D_CAPTURE");
        if (!string.IsNullOrEmpty(capturePath))
        {
            _ = _view.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(5000);
                await CapturePreviewAsync(capturePath);
            });
        }
#endif
        return true;
    }

    public void Stop() => _timer.Stop();

    /// <summary>Debug aid: renders the current page to a PNG (used to verify the 3D scene headlessly).</summary>
    public async Task CapturePreviewAsync(string path)
    {
        if (_view.CoreWebView2 is null) return;
        using var stream = File.Create(path);
        await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }

    public void PushState()
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetStateJson()); }
        catch { /* view torn down */ }
    }

    public void PushLibrary(string kind)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetLibraryJson(kind)); }
        catch { /* view torn down */ }
    }

    public void PushArtwork()
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetArtworkJson()); }
        catch { /* view torn down */ }
    }

    public void PushDetail(string id)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetDetailJson(id)); }
        catch { /* view torn down */ }
    }

    public void PushCardArtwork(string key)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetCardArtworkJson(key)); }
        catch { /* view torn down */ }
    }

    public void PushLyrics()
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetLyricsJson()); }
        catch { /* view torn down */ }
    }

    public void PushLyricIndex()
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetLyricIndexJson()); }
        catch { /* view torn down */ }
    }

    public void PushSettings()
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(_actions.ThreeDGetSettingsJson()); }
        catch { /* view torn down */ }
    }

    private async Task InitializeAsync()
    {
        if (_view.CoreWebView2 is not null)
        {
            IsReady = true;
            return;
        }

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XstarMusic", "WebView2");
        Directory.CreateDirectory(userData);

        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await _view.EnsureCoreWebView2Async(environment);

        _view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 8, 8, 14);

        var core = _view.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 CoreWebView2 was not initialized.");
        _core = core;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnWebMessage;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "ThreeD", "web");
        if (!Directory.Exists(webRoot))
            throw new DirectoryNotFoundException($"3D web assets not found: {webRoot}");

        core.SetVirtualHostNameToFolderMapping(VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate(HomeUrl);
        IsReady = true;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_core is null) return;
        if (!_player.Snapshot.IsPlaying) return;

        var now = DateTime.Now;
        double dt = (now - _lastTick).TotalMilliseconds;
        _lastTick = now;

        var frame = _pump.Next(dt);
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(frame, ThreeDJson.Options)); }
        catch { return; }

        if (++_ticksSinceState >= 15)
        {
            _ticksSinceState = 0;
            PushState();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        ThreeDActionMessage? message;
        try { message = JsonSerializer.Deserialize<ThreeDActionMessage>(e.WebMessageAsJson, ThreeDJson.Options); }
        catch { return; }
        if (message is null) return;

        switch (message.Action)
        {
            case "ready":
                PushArtwork();
                PushLyrics();
                PushLyricIndex();
                PushSettings();
                PushState();
                break;
            case "getState": PushState(); break;
            case "getLibrary": PushLibrary(message.Kind ?? "albums"); break;
            case "getDetail": PushDetail(message.Id ?? ""); break;
            case "getCardArtwork": PushCardArtwork(message.Id ?? ""); break;
            case "play": _actions.ThreeDPlay(); break;
            case "pause": _actions.ThreeDPause(); break;
            case "next": _actions.ThreeDNext(); break;
            case "prev": _actions.ThreeDPrev(); break;
            case "seek": _actions.ThreeDSeek(message.PositionMs ?? 0); break;
            case "volume": _actions.ThreeDSetVolume(message.Volume ?? 0); break;
            case "playTrack": _actions.ThreeDPlayTrack(message.Id ?? ""); break;
            case "setDisplayMode": _actions.ThreeDSetDisplayMode(message.Mode ?? "normal"); break;
            case "windowDrag": _actions.ThreeDWindowDrag(); break;
            case "windowResize": _actions.ThreeDWindowResize(message.Edge ?? ""); break;
            case "windowMinimize": _actions.ThreeDMinimize(); break;
            case "windowMaximize": _actions.ThreeDMaximize(); break;
            case "windowClose": _actions.ThreeDClose(); break;
            case "setSetting": _actions.ThreeDOnSetting(message.Key ?? "", message.Value); break;
            case "pickBackgroundImage": _actions.ThreeDPickBackgroundImage(); break;
            case "clearBackgroundImage": _actions.ThreeDClearBackgroundImage(); break;
            case "clientError": LogClientError(message.Message); break;
        }
    }

    /// <summary>Records a JS/uncaught error reported by the page (diagnostics for support).</summary>
    public static void LogClientError(string? message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XstarMusic");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "3d-client.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_core is not null)
        {
            _core.WebMessageReceived -= OnWebMessage;
            _core = null;
        }
    }
}
