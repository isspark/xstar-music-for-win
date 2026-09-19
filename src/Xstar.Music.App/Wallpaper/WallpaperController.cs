using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Xstar.Music.App.ThreeD;
using Xstar.Music.Core;

namespace Xstar.Music.App.Wallpaper;

/// <summary>
/// Owns the desktop-wallpaper host. In wallpaper mode a dedicated opaque window with its own
/// WebView2 is reparented under Explorer's WorkerW so the 3D scene renders behind the desktop
/// icons (mouse passes through). In interactive mode the window is a normal top-level fullscreen
/// window above the icons so the mouse can drag/zoom it. Independent of the main window, so it
/// keeps running while the player is hidden to the tray.
/// </summary>
public sealed class WallpaperController : IDisposable
{
    private const string WallpaperUrl = "https://xstar.local/index.html?wallpaper=1";
    private const int WmDisplayChange = 0x007E;

    private readonly IPlayerEngine _player;
    private readonly IThreeDActions _actions;
    private readonly WallpaperMouseHook _mouseHook;
    private WallpaperWindow? _window;
    private HwndSource? _windowSource;
    private ThreeDHost? _host;
    private HwndSource? _messageSource;
    private uint _taskbarCreatedMessage;

    public WallpaperController(IPlayerEngine player, IThreeDActions actions)
    {
        _player = player;
        _actions = actions;
        // Empty-desktop drag / wheel is forwarded to the web scene as camera orbit / zoom.
        _mouseHook = new WallpaperMouseHook(
            (dx, dy) => _host?.PushOrbit(dx, dy, 0),
            wheel => _host?.PushOrbit(0, 0, wheel))
        {
            Debug = Log
        };
    }

    public bool IsRunning { get; private set; }
    public bool IsInteractive { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Opens the wallpaper (behind the icons) or interactive (above the icons) 3D window.</summary>
    public async Task<bool> StartAsync(bool interactive)
    {
        if (IsRunning) return true;
        LastError = null;

        var window = new WallpaperWindow();
        _window = window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        // Wallpaper mode is anchored to the desktop layer before the WebView2 initializes.
        if (!interactive && !DesktopWallpaper.TryAttach(hwnd, out var attachError))
        {
            LastError = attachError;
            Log($"attach failed: {attachError}");
            window.Close();
            _window = null;
            return false;
        }

        window.Closed += (_, _) => Log("window closed");
        window.Show();
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            _windowSource = source;
            source.AddHook(WindowHook);
        }

        var host = new ThreeDHost(
            window.WallpaperWebView,
            _player,
            _actions,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XstarMusic",
                "WebView2Wallpaper"),
            WallpaperUrl);
        _host = host;

        if (!await host.StartAsync())
        {
            LastError = host.LastError;
            Log($"webview start failed: {LastError}");
            Stop();
            return false;
        }

        if (interactive)
        {
            DesktopWallpaper.EnterInteractive(hwnd);
            window.Activate();
        }
        else
        {
            // WebView2 creates its child HWNDs during initialization; re-apply the bounds and
            // click-through styles now that they exist. Keep WS_EX_TRANSPARENT: on Win11 the
            // desktop wallpaper layer composites above normal child windows, so clearing it hides
            // the scene �?which is exactly why interaction needs a separate top-level window.
            DesktopWallpaper.FitPrimary(hwnd);
            DesktopWallpaper.ApplyClickThrough(hwnd);

            // Dragging/wheeling on empty desktop space rotates the scene, without covering the icons.
            _mouseHook.Install();
            _mouseHook.Enabled = true;
            Log($"mouse hook installed={_mouseHook.Installed}");
        }
        EnsureTaskbarHook();
        IsRunning = true;
        IsInteractive = interactive;
        Log($"started: hwnd=0x{hwnd:X} interactive={interactive} attached={DesktopWallpaper.IsAttached(hwnd)}");
        return true;
    }

    /// <summary>Switches between wallpaper (behind icons) and interactive (above icons) modes.</summary>
    public async Task SetInteractiveAsync(bool interactive)
    {
        if (!IsRunning)
        {
            IsInteractive = interactive;
            return;
        }
        if (interactive == IsInteractive) return;

        // Re-parenting the live WPF window between the desktop layer and the top level does not
        // render reliably, so rebuild the host window in the target mode instead.
        Stop();
        await StartAsync(interactive);
    }

    public void Stop()
    {
        if (!IsRunning && _window is null) return;
        Log("stopped");

        _mouseHook.Uninstall();
        var hwnd = _window is null ? IntPtr.Zero : new WindowInteropHelper(_window).Handle;
        _host?.Dispose();
        _host = null;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowHook);
            _windowSource = null;
        }
        if (hwnd != IntPtr.Zero) DesktopWallpaper.Detach(hwnd);
        _window?.Close();
        _window = null;
        DisposeMessageSource();
        IsRunning = false;
        IsInteractive = false;
    }

    /// <summary>Re-pushes the shared 3D appearance settings to a running wallpaper.</summary>
    public void PushSettings() => _host?.PushSettings();

    public void PushState() => _host?.PushState();

    public void PushArtwork() => _host?.PushArtwork();

    public void PushLyrics() => _host?.PushLyrics();

    public void PushLyricIndex() => _host?.PushLyricIndex();

    private void EnsureTaskbarHook()
    {
        if (_messageSource is not null) return;
        var parameters = new HwndSourceParameters("XstarWallpaperMessage")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(MessageHook);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange)
        {
            // Resolution / monitor layout changed �?refit and re-anchor to the desktop layer.
            _window?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_window is null) return;
                var handle = new WindowInteropHelper(_window).Handle;
                if (handle == IntPtr.Zero) return;
                if (IsInteractive) DesktopWallpaper.EnterInteractive(handle);
                else
                {
                    DesktopWallpaper.TryAttach(handle, out _);
                    DesktopWallpaper.ApplyClickThrough(handle);
                }
            }), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            // Explorer restarted and recreated its desktop windows �?re-parent onto the new WorkerW.
            // In interactive mode the window is top-level, so there is nothing to re-anchor.
            if (!IsInteractive)
                _window?.Dispatcher.BeginInvoke(new Action(Reattach), DispatcherPriority.Background);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Reattach()
    {
        if (_window is null || IsInteractive) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        DesktopWallpaper.TryAttach(hwnd, out _);
        DesktopWallpaper.ApplyClickThrough(hwnd);
    }

    private void DisposeMessageSource()
    {
        if (_messageSource is null) return;
        _messageSource.RemoveHook(MessageHook);
        _messageSource.Dispose();
        _messageSource = null;
    }

    public void Dispose()
    {
        Stop();
        DisposeMessageSource();
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XstarMusic");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "wallpaper.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);
}
