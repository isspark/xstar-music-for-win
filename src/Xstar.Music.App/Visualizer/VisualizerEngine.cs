using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xstar.Music.Core;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// Owns the 30 fps render timer, reads audio frames from IPlayerEngine,
/// assembles a VisualizerFrame and dispatches it to the currently active renderer.
/// Lifecycle is tied to MainWindow — Start/Stop called when entering/leaving visualizer mode.
/// </summary>
public sealed class VisualizerEngine : IDisposable
{
    private readonly IPlayerEngine _player;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private IVisualizerRenderer? _renderer;
    private string _styleId = "stardust";
    private readonly List<IVisualizerRenderer> _allRenderers = new();
    private readonly long[] _tickCounts = new long[1]; // placeholder for high-res timing
    private double _lastBeatTimeMs;
    private double _totalTimeMs;  // cumulative time since engine started
    private float _bassEnergyEma;
    private DateTime _lastFrame = DateTime.Now;

    public IReadOnlyList<IVisualizerRenderer> AllRenderers => _allRenderers;

    /// <summary>Current render target bitmap — MainWindow binds VisualizerImage.Source to this.</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    public VisualizerEngine(IPlayerEngine player)
    {
        _player = player;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += OnTick;

        // Register all 7 renderers
        _allRenderers.Add(new BarsRenderer());
        _allRenderers.Add(new RadialRenderer());
        _allRenderers.Add(new WaveRenderer());
        _allRenderers.Add(new StardustRenderer());
        _allRenderers.Add(new GalaxyRenderer());
        _allRenderers.Add(new NebulaRenderer());
        _allRenderers.Add(new AuroraRenderer());
    }

    /// <summary>Build / rebuild the render target bitmap. Call when Stage size changes.</summary>
    public void SetBitmapSize(int width, int height)
    {
        width = Math.Max(100, width);
        height = Math.Max(100, height);
        var newBmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _bitmap = newBmp;
        _renderer?.Initialize(_bitmap);
    }

    /// <summary>Sets the active renderer by StyleId. Safe to call while running.</summary>
    public void SetStyle(string styleId)
    {
        if (_styleId == styleId && _renderer is not null) return;
        var next = _allRenderers.FirstOrDefault(r => r.StyleId == styleId);
        next ??= _allRenderers.First();
        _styleId = next.StyleId;
        _renderer?.Cleanup();
        _renderer = next;
        if (_bitmap is not null) _renderer.Initialize(_bitmap);
    }

    public string CurrentStyleId => _styleId;

    /// <summary>Start the render loop. Call when entering visualizer display mode.</summary>
    public void Start(bool reduceMotion)
    {
        if (_bitmap is null)
        {
            SetBitmapSize(RendererUtils.RenderWidth, RendererUtils.RenderHeight);
        }
        // Ensure current renderer is initialised
        if (_renderer is null) SetStyle(_styleId);
        _lastFrame = DateTime.Now;
        _bassEnergyEma = 0;
        _lastBeatTimeMs = 0;
        _totalTimeMs = 0;

        _timer.Interval = TimeSpan.FromMilliseconds(reduceMotion ? 100 : 33);
        _timer.Start();
    }

    /// <summary>Stop the render loop and freeze the last frame.</summary>
    public void Stop()
    {
        _timer.Stop();
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Current accent colour, pushed every frame.</summary>
    public Color AccentColor { get; set; } = Colors.DodgerBlue;

    /// <summary>Theme-derived backdrop colour, pushed every frame.</summary>
    public Color BackgroundColor { get; set; } = RendererUtils.DefaultBackground;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_bitmap is null || _renderer is null) return;
        if (!_player.Snapshot.IsPlaying)
        {
            // Keep last frame, but refresh to keep UI thread alive minimally
            return;
        }

        var now = DateTime.Now;
        double dtMs = (now - _lastFrame).TotalMilliseconds;
        _lastFrame = now;

        // Gather audio data. Spectrum and waveform are independent — a renderer
        // should still draw even if only one source is available.
        var spectrum = _player.GetSpectrumData(64);
        var waveform = _player.GetWaveformData(512);
        if (spectrum.Length == 0 && waveform.Length == 0)
        {
            // Both engines refused to provide data — render static fallback
            _renderer.Render(new VisualizerFrame(
                Array.Empty<float>(), Array.Empty<float>(),
                0, 0, 0, false, AccentColor, BackgroundColor, dtMs));
            return;
        }

        // Derive band energies
        float bass = 0, mid = 0, treble = 0;
        int bassCount = 0, midCount = 0, trebleCount = 0;
        for (int i = 0; i < spectrum.Length; i++)
        {
            float s = spectrum[i];
            if (i < 16) { bass += s; bassCount++; }
            else if (i < 44) { mid += s; midCount++; }
            else { treble += s; trebleCount++; }
        }
        if (bassCount > 0) bass /= bassCount;
        if (midCount > 0) mid /= midCount;
        if (trebleCount > 0) treble /= trebleCount;

        // Smooth bass
        const float bassAlpha = 0.2f;
        _bassEnergyEma = bass * bassAlpha + _bassEnergyEma * (1 - bassAlpha);

        // Beat detection — cumulative time-based interval check
        _totalTimeMs += dtMs;
        bool beatDetected = false;
        if (bass > _bassEnergyEma * 2.0 && bass > 0.25f && _totalTimeMs - _lastBeatTimeMs > 200)
        {
            beatDetected = true;
            _lastBeatTimeMs = _totalTimeMs;
        }

        var frame = new VisualizerFrame(
            spectrum, waveform, bass, mid, treble, beatDetected, AccentColor, BackgroundColor, dtMs);

        _renderer.Render(frame);
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var r in _allRenderers) r.Cleanup();
    }
}
