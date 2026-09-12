using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// Per-frame data assembled by VisualizerEngine from the player engine.
/// All arrays are already normalized (0~1 or -1~1).
/// </summary>
public sealed record VisualizerFrame(
    float[] Spectrum,      // 64 bands, 0~1
    float[] Waveform,      // PCM waveform, -1~1
    float BassEnergy,      // low-frequency energy, 0~1
    float MidEnergy,       // mid-frequency energy, 0~1
    float TrebleEnergy,    // high-frequency energy, 0~1
    bool BeatDetected,     // true this frame if a beat / kick was detected
    Color Accent,          // current accent colour from ThemeManager
    Color Background,      // theme-derived backdrop colour renderers clear with
    double DeltaTimeMs     // ms since the previous frame
);

/// <summary>
/// Strategy interface for audio-visual rendering. Each visualizer style
/// implements this and writes directly into the provided WriteableBitmap.
/// </summary>
public interface IVisualizerRenderer
{
    string StyleId { get; }     // "bars", "radial", "stardust", ...
    string DisplayName { get; }  // Chinese name shown in the UI
    bool IsHardwareCompatible { get; } // true when the engine supports FFT

    void Initialize(WriteableBitmap bitmap);
    void Render(VisualizerFrame frame);
    void Cleanup();
}
