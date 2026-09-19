using System.Windows;

namespace Xstar.Music.App.Wallpaper;

/// <summary>
/// Opaque, borderless host window for the wallpaper WebView2. Never layered: in wallpaper mode it
/// is reparented behind the desktop icons, and in interactive mode it is a top-level fullscreen
/// window whose child HWNDs stay click-through so the WebView2 composites above the desktop.
/// </summary>
public partial class WallpaperWindow : Window
{
    public WallpaperWindow()
    {
        InitializeComponent();

        // WPF otherwise enforces the XAML 1280x720 and fights the Win32 fullscreen sizing.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }
}
