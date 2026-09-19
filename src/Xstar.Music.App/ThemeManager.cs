using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Xstar.Music.App;

/// <summary>
/// Drives the WPF UI (Fluent) theme, the named theme presets and the runtime,
/// cover-derived accent colour. Light/dark follows the Windows setting by default.
/// </summary>
public static class ThemeManager
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>A named, ready-to-apply theme: accent colour + default mode + window backdrop.</summary>
    public sealed record ThemePreset(string Id, string Name, Color Accent, ThemeMode Mode, WindowBackdropType Backdrop, string Description);

    public static readonly IReadOnlyList<ThemePreset> Presets = new List<ThemePreset>
    {
        new("deepspace", "深空", Color.FromRgb(0x5B, 0x8C, 0xFF), ThemeMode.Dark, WindowBackdropType.Mica, "靛蓝 · 冷静克制"),
        new("aurora", "极光", Color.FromRgb(0x2E, 0xD3, 0xB7), ThemeMode.Dark, WindowBackdropType.Mica, "青绿 · 通透轻盈"),
        new("cyber", "霓虹", Color.FromRgb(0xB1, 0x5C, 0xFF), ThemeMode.Dark, WindowBackdropType.Acrylic, "紫罗兰 · 高饱和"),
        new("forest", "森林", Color.FromRgb(0x3D, 0xD6, 0x8C), ThemeMode.Dark, WindowBackdropType.Tabbed, "翡翠 · 沉静自然"),
        new("sunset", "日落", Color.FromRgb(0xFF, 0x7A, 0x59), ThemeMode.Dark, WindowBackdropType.Mica, "珊瑚 · 温暖柔和"),
        new("graphite", "石墨", Color.FromRgb(0x9A, 0xA3, 0xAE), ThemeMode.Dark, WindowBackdropType.Tabbed, "中性灰 · 极简"),
        new("glacier", "冰晶", Color.FromRgb(0x2F, 0x80, 0xED), ThemeMode.Light, WindowBackdropType.Mica, "晴空蓝 · 明亮"),
        new("blossom", "樱粉", Color.FromRgb(0xE8, 0x54, 0x8B), ThemeMode.Light, WindowBackdropType.Acrylic, "玫瑰粉 · 温柔"),
    };

    // Theme-aware brush palette: (resource key, dark hex, light hex). Swapped on every theme change.
    private static readonly (string Key, string Dark, string Light)[] ThemeBrushPalette =
    {
        ("PageOverlayBrush", "#B80B0C10", "#C7F6F7FB"),
        ("PagePanelBrush", "#1AFFFFFF", "#0F000000"),
        ("QualityNormalBgBrush", "#1FFFFFFF", "#12000000"),
        ("QualityNormalFgBrush", "#B9C0D0", "#4B5563"),
        ("QualityHighBgBrush", "#263B82F6", "#1A3B82F6"),
        ("QualityHighFgBrush", "#8FB8FF", "#1D4ED8"),
        ("QualityLosslessBgBrush", "#2634D399", "#1A16A34A"),
        ("QualityLosslessFgBrush", "#6EE7B7", "#15803D"),
        ("QualityHiresBgBrush", "#26F59E0B", "#1AF59E0B"),
        ("QualityHiresFgBrush", "#FCD34D", "#B45309"),
        ("QualityOnlineBgBrush", "#266E8BFF", "#1A6E8BFF"),
        ("QualityOnlineFgBrush", "#A9BBFF", "#4338CA"),
        ("SongSelectionBrush", "#33FFFFFF", "#14000000"),
    };

    private static readonly ThemePreset Fallback = Presets[0];
    private static ThemeMode _mode = ThemeMode.System;
    private static ThemePreset _preset = Fallback;
    private static DispatcherTimer? _watcher;
    private static bool _lastSystemLight;

    public static bool IsLight { get; private set; }
    public static Color AccentColor { get; private set; } = Fallback.Accent;
    public static ThemeMode Mode => _mode;
    public static ThemePreset CurrentPreset => _preset;
    public static event Action? Changed;

    public static ThemePreset GetPreset(string? id)
        => Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Fallback;

    public static void Initialize(ThemeMode mode)
    {
        _lastSystemLight = ReadSystemLight();
        _watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watcher.Tick += (_, _) =>
        {
            if (_mode != ThemeMode.System) return;
            var light = ReadSystemLight();
            if (light == _lastSystemLight) return;
            _lastSystemLight = light;
            ApplyTheme();
        };
        _watcher.Start();
        SetMode(mode);
    }

    public static void ApplyPreset(ThemePreset preset)
    {
        _preset = preset ?? Fallback;
        _mode = _preset.Mode;
        AccentColor = _preset.Accent;
        ApplyTheme();
    }

    public static void SetMode(ThemeMode mode)
    {
        _mode = mode;
        ApplyTheme();
    }

    public static void ApplyAccent(Color color)
    {
        AccentColor = color;
        ApplyAccentCore(color);
    }

    public static void ResetAccent() => ApplyAccent(_preset.Accent);

    private static void ApplyTheme()
    {
        IsLight = _mode == ThemeMode.Light || (_mode == ThemeMode.System && ReadSystemLight());
        var theme = IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        // The main window uses AllowsTransparency for whole-window opacity; DWM system backdrops
        // (Mica/Acrylic) cannot be combined with a layered window, so always request None.
        try { ApplicationThemeManager.Apply(theme, WindowBackdropType.None, false); }
        catch { try { ApplicationThemeManager.Apply(theme, WindowBackdropType.None); } catch { } }
        ApplyAccentCore(AccentColor);
        ApplyThemeBrushes(IsLight);
        Changed?.Invoke();
    }

    private static void ApplyThemeBrushes(bool light)
    {
        if (Application.Current is not { } app) return;
        foreach (var (key, dark, lightHex) in ThemeBrushPalette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? lightHex : dark));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static void ApplyAccentCore(Color color)
    {
        var theme = IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        try { ApplicationAccentColorManager.Apply(color, theme, false, false); }
        catch { try { ApplicationAccentColorManager.Apply(color); } catch { } }
    }

    private static bool ReadSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
