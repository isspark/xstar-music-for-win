using System.Runtime.InteropServices;

namespace Xstar.Music.App.Wallpaper;

/// <summary>
/// Win32 helpers for hosting the wallpaper. Wallpaper mode reparents an opaque window under
/// Explorer's <c>WorkerW</c> so it renders behind the desktop icons; interactive mode keeps the
/// window top-level and fullscreen so it sits above the icons and can receive the mouse.
/// </summary>
internal static class DesktopWallpaper
{
    private const int WmSpawnWorkerW = 0x052C;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint MonitorDefaultToPrimary = 1;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    // GA_PARENT ignores ownership and returns the real parent, unlike GetParent (which returns
    // the owner — 0 — for a non-WS_CHILD window).
    public static bool IsAttached(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var parent = GetAncestor(hwnd, 1);
        return parent != IntPtr.Zero && parent != GetDesktopWindow();
    }

    /// <summary>Reparents <paramref name="hwnd"/> under the desktop WorkerW and fills the primary monitor.</summary>
    public static bool TryAttach(IntPtr hwnd, out string? error)
    {
        error = null;
        if (hwnd == IntPtr.Zero)
        {
            error = "window handle is null";
            return false;
        }

        var worker = FindWorkerW();
        if (worker == IntPtr.Zero)
        {
            error = "WorkerW not found (Explorer desktop unavailable)";
            return false;
        }

        ApplyExStyle(hwnd, interactive: false);
        var previous = SetParent(hwnd, worker);
        if (previous == IntPtr.Zero)
        {
            var code = Marshal.GetLastWin32Error();
            if (code != 0)
            {
                error = $"SetParent failed (win32 {code})";
                return false;
            }
        }

        FitPrimary(hwnd);
        SetDescendantsClickThrough(hwnd, clickThrough: true);
        return true;
    }

    public static void Detach(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) SetParent(hwnd, IntPtr.Zero);
    }

    /// <summary>
    /// Makes the (top-level, never-reparented) window a fullscreen interactive overlay above the
    /// desktop icons, clearing the click-through styles so it receives the mouse.
    /// </summary>
    public static void EnterInteractive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        ex &= ~(WsExTransparent | WsExNoActivate);
        ex |= WsExToolWindow;
        SetWindowLong(hwnd, GwlExStyle, ex);
        SetDescendantsClickThrough(hwnd, clickThrough: true);

        var bounds = PrimaryMonitorBounds();
        // Raise above other normal windows, then drop the topmost flag so it does not cover every
        // app permanently — it stays at the top of the normal z-order, above the desktop.
        SetWindowPos(hwnd, HwndTopmost, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SwpShowWindow);
        SetWindowPos(hwnd, HwndNotTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    /// <summary>Resizes the window to cover the primary monitor in physical pixels.</summary>
    public static void FitPrimary(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var bounds = PrimaryMonitorBounds();
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>Re-applies the wallpaper click-through styles (also to newly created child HWNDs).</summary>
    public static void ApplyClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ApplyExStyle(hwnd, interactive: false);
        SetDescendantsClickThrough(hwnd, clickThrough: true);
    }

    private static void SetDescendantsClickThrough(IntPtr parent, bool clickThrough)
    {
        if (parent == IntPtr.Zero) return;
        EnumChildWindows(parent, (child, _) =>
        {
            var ex = GetWindowLong(child, GwlExStyle);
            ex = clickThrough ? ex | WsExTransparent : ex & ~WsExTransparent;
            SetWindowLong(child, GwlExStyle, ex);
            return true;
        }, IntPtr.Zero);
    }

    private static void ApplyExStyle(IntPtr hwnd, bool interactive)
    {
        var ex = GetWindowLong(hwnd, GwlExStyle);
        ex |= WsExNoActivate | WsExToolWindow;
        if (interactive) ex &= ~WsExTransparent;
        else ex |= WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, ex);
    }

    private static NativeRect PrimaryMonitorBounds()
    {
        var monitor = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcMonitor;
        return new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /* ===================== desktop hit-testing (for empty-desktop drag) ===================== */

    private const int LvmHitTest = 0x1012;
    private const uint LvhtOnItem = 0x0000000E;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct LvHitTestInfo
    {
        public NativePoint pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint pt);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, out IntPtr read);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public static string DescribePoint(int x, int y)
    {
        var list = DesktopListView();
        var hwnd = WindowFromPoint(new NativePoint { X = x, Y = y });
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        return $"pt=({x},{y}) hwnd=0x{hwnd:X} class='{sb}' list=0x{list:X} primary={IsOnPrimary(x, y)} " +
               $"desktop={IsDesktopPoint(x, y)} icon={IsOverDesktopIcon(x, y)}";
    }

    /// <summary>True when the point lies on the primary monitor.</summary>
    public static bool IsOnPrimary(int x, int y)
    {
        var b = PrimaryMonitorBounds();
        return x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom;
    }

    /// <summary>True when the top-most window at the point is the desktop's icon list view.</summary>
    public static bool IsDesktopPoint(int x, int y)
    {
        var list = DesktopListView();
        if (list == IntPtr.Zero) return false;
        var hwnd = WindowFromPoint(new NativePoint { X = x, Y = y });
        return hwnd == list;
    }

    /// <summary>True when the point is over a desktop icon (list item) rather than empty space.</summary>
    public static bool IsOverDesktopIcon(int x, int y)
    {
        var list = DesktopListView();
        if (list == IntPtr.Zero) return false;
        GetWindowThreadProcessId(list, out var pid);
        if (pid == 0) return false;

        var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, pid);
        if (process == IntPtr.Zero) return false;
        var size = Marshal.SizeOf<LvHitTestInfo>();
        var remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)size, MemCommit | MemReserve, PageReadWrite);
        if (remote == IntPtr.Zero)
        {
            CloseHandle(process);
            return false;
        }

        var local = Marshal.AllocHGlobal(size);
        try
        {
            var info = new LvHitTestInfo { pt = new NativePoint { X = x, Y = y } };
            Marshal.StructureToPtr(info, local, false);
            if (!WriteProcessMemory(process, remote, local, (IntPtr)size, out _)) return false;

            if (SendMessageTimeout(list, LvmHitTest, IntPtr.Zero, remote, 0x0002 /*SMTO_ABORTIFHUNG*/, 100, out _) == IntPtr.Zero)
                return false;

            if (!ReadProcessMemory(process, remote, local, (IntPtr)size, out _)) return false;
            var result = Marshal.PtrToStructure<LvHitTestInfo>(local);
            return (result.flags & LvhtOnItem) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            VirtualFreeEx(process, remote, IntPtr.Zero, MemRelease);
            CloseHandle(process);
        }
    }

    private static IntPtr DesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var defView = progman != IntPtr.Zero
            ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;
        if (defView == IntPtr.Zero)
        {
            EnumWindows((top, _) =>
            {
                var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero) defView = view;
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static IntPtr FindWorkerW()
    {
        var progman = FindWindow("Progman", null);
        SendMessageTimeout(progman, WmSpawnWorkerW, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // Classic layout (Windows 10): SHELLDLL_DefView lives under a top-level WorkerW, and the
        // wallpaper WorkerW is the next top-level sibling in Z-order.
        IntPtr worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                var candidate = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                if (candidate != IntPtr.Zero) worker = candidate;
            }
            return true;
        }, IntPtr.Zero);
        if (worker != IntPtr.Zero) return worker;

        // Windows 11 layout: SHELLDLL_DefView and the wallpaper WorkerW are both direct children
        // of Progman, with the WorkerW placed after (i.e. below) the def view.
        if (progman != IntPtr.Zero)
        {
            var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                worker = FindWindowEx(progman, defView, "WorkerW", null);
                if (worker != IntPtr.Zero) return worker;
            }
            worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        }
        return worker;
    }
}
