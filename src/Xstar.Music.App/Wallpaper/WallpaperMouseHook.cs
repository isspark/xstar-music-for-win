using System.Runtime.InteropServices;

namespace Xstar.Music.App.Wallpaper;

/// <summary>
/// Global low-level mouse hook that turns left-drags / wheel on empty desktop space into 3D camera
/// orbit / zoom, without covering the desktop icons. Clicks on icons, on other windows, and the
/// right mouse button pass through untouched, so the desktop keeps working normally.
/// </summary>
internal sealed class WallpaperMouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmMouseWheel = 0x020A;
    private const int DragThreshold = 3;

    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("XSTAR_WALLPAPER_HOOK_DEBUG") == "1";

    private readonly Action<int, int> _orbit;
    private readonly Action<int> _zoom;
    private IntPtr _hook;
    private HookProc? _proc;
    private bool _dragging;
    private bool _pending;
    private int _startX, _startY, _lastX, _lastY;

    public WallpaperMouseHook(Action<int, int> orbit, Action<int> zoom)
    {
        _orbit = orbit;
        _zoom = zoom;
    }

    public bool Enabled { get; set; }

    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>Optional diagnostic sink (used to debug the hit-test decisions).</summary>
    public Action<string>? Debug { get; set; }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        _dragging = false;
        _pending = false;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !Enabled) return CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var x = data.X;
            var y = data.Y;
            switch (wParam.ToInt32())
            {
                case WmLButtonDown:
                    _dragging = false;
                    _pending = false;
                    if (Trace) Debug?.Invoke("down " + DesktopWallpaper.DescribePoint(x, y));
                    if (IsEmptyDesktopPoint(x, y))
                    {
                        _pending = true;
                        _startX = _lastX = x;
                        _startY = _lastY = y;
                        return 1; // swallow so the desktop does not start a rubber-band selection
                    }
                    break;

                case WmMouseMove:
                    if (!_pending && !_dragging) break;
                    if (!_dragging)
                    {
                        if (Math.Abs(x - _startX) < DragThreshold && Math.Abs(y - _startY) < DragThreshold)
                            return 1; // still within the click threshold
                        _dragging = true;
                    }
                    var dx = x - _lastX;
                    var dy = y - _lastY;
                    _lastX = x;
                    _lastY = y;
                    if (dx != 0 || dy != 0) _orbit(dx, dy);
                    return 1;

                case WmLButtonUp:
                    if (_pending || _dragging)
                    {
                        _pending = false;
                        _dragging = false;
                        return 1;
                    }
                    break;

                case WmMouseWheel:
                    if (IsEmptyDesktopPoint(x, y))
                    {
                        _zoom((short)((data.MouseData >> 16) & 0xFFFF));
                        return 1;
                    }
                    break;
            }
        }
        catch { /* a hook must never throw */ }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsEmptyDesktopPoint(int x, int y)
        => DesktopWallpaper.IsOnPrimary(x, y)
           && DesktopWallpaper.IsDesktopPoint(x, y)
           && !DesktopWallpaper.IsOverDesktopIcon(x, y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public void Dispose() => Uninstall();
}
