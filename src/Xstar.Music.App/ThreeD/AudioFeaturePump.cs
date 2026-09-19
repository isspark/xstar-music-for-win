using Xstar.Music.Core;

namespace Xstar.Music.App.ThreeD;

/// <summary>
/// Extracts spectrum band energies, smoothed bass and beat detection from the player
/// engine for the 3D web view. Mirrors VisualizerEngine's frame assembly; the two can
/// be unified once the 3D pipeline settles.
/// </summary>
public sealed class AudioFeaturePump
{
    private const float BassAlpha = 0.2f;

    private readonly IPlayerEngine _player;
    private float _bassEma;
    private double _lastBeatMs;
    private double _totalMs;

    public AudioFeaturePump(IPlayerEngine player) => _player = player;

    /// <summary>Builds the next audio frame. Returns an empty frame when no FFT data is available.</summary>
    public ThreeDAudioMessage Next(double dtMs)
    {
        var spectrum = _player.GetSpectrumData(64);
        if (spectrum.Length == 0)
            return new ThreeDAudioMessage { DtMs = dtMs };

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

        _bassEma = bass * BassAlpha + _bassEma * (1 - BassAlpha);

        _totalMs += dtMs;
        bool beat = false;
        if (bass > _bassEma * 2.0 && bass > 0.25f && _totalMs - _lastBeatMs > 200)
        {
            beat = true;
            _lastBeatMs = _totalMs;
        }

        return new ThreeDAudioMessage
        {
            Spectrum = spectrum,
            Bass = bass,
            Mid = mid,
            Treble = treble,
            Beat = beat,
            DtMs = dtMs
        };
    }

    /// <summary>Resets smoothing / beat state (call when (re)entering 3D mode).</summary>
    public void Reset()
    {
        _bassEma = 0;
        _totalMs = 0;
        _lastBeatMs = 0;
    }
}
