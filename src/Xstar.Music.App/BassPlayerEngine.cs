using System.IO;
using ManagedBass;
using ManagedBass.DirectX8;
using Xstar.Music.Core;

namespace Xstar.Music.App;

public sealed class BassPlayerEngine : IPlayerEngine
{
    private static readonly string[] PluginFiles = { "bassflac.dll", "bassopus.dll", "bassalac.dll", "bassape.dll" };
    private static readonly double[] EqBands = { 80, 125, 250, 500, 1000, 2000, 4000, 8000, 12000, 16000 };
    public static int EqBandCount => EqBands.Length;
    public static double GetEqBandFrequency(int band) => EqBands[band];

    private static bool _initialized;
    private readonly SyncProcedure _endSync;
    private int _stream;
    private Song? _song;
    private bool _isPlaying;
    private double _volume = 1;
    private TimeSpan _duration;
    private PlaybackMode _mode = PlaybackMode.Sequential;

    // EQ state
    private DXParamEQ? _eqFx;
    private int[] _eqBandIndices = Array.Empty<int>();
    private bool _eqEnabled;
    private readonly double[] _eqGains = new double[EqBands.Length];

    // Visualizer state
    private float[]? _spectrumSmooth;
    private float[]? _waveformSmooth;
    // DSP capture (reliable waveform source on device-playback streams)
    private DSPProcedure? _dspCaptureProc;
    private int _dspCaptureHandle;
    private readonly object _dspLock = new();
    private readonly float[] _dspMono = new float[DspMaxFrames];
    private int _dspCount;
    private const int DspMaxFrames = 8192;

    public BassPlayerEngine()
    {
        _endSync = OnEnded;
        if (_initialized) return;
        if (!Bass.Init(-1, 44100, DeviceInitFlags.Default))
            throw new InvalidOperationException($"BASS 初始化失败：{Bass.LastError}");
        foreach (var plugin in PluginFiles)
        {
            var path = Path.Combine(AppContext.BaseDirectory, plugin);
            if (File.Exists(path)) Bass.PluginLoad(path);
        }
        _initialized = true;
    }

    public string Name => "BASS";

    public PlayerSnapshot Snapshot => new(_song?.Id, _isPlaying, Position, _duration, _mode);

    public event EventHandler? SnapshotChanged;
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    private TimeSpan Position
    {
        get
        {
            if (_stream == 0) return TimeSpan.Zero;
            var bytes = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
            var seconds = bytes > 0 ? Bass.ChannelBytes2Seconds(_stream, bytes) : 0;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
    }

    public bool Load(Song song)
    {
        FreeStream();
        _song = song;
        _stream = IsRemote(song.Path)
            ? Bass.CreateStream(song.Path, 0, BassFlags.Default, null, IntPtr.Zero)
            : Bass.CreateStream(song.Path, 0, 0, BassFlags.Default);
        if (_stream == 0)
        {
            Failed?.Invoke(this, $"无法打开音频（{Bass.LastError}）");
            return false;
        }
        var length = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
        var seconds = length > 0 ? Bass.ChannelBytes2Seconds(_stream, length) : 0;
        _duration = seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : (song.DurationMs > 0 ? TimeSpan.FromMilliseconds(song.DurationMs) : TimeSpan.Zero);
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, (float)_volume);
        ApplyEqToStream();
        AttachDspCapture();
        _isPlaying = false;
        Opened?.Invoke(this, EventArgs.Empty);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsRemote(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Play()
    {
        if (_stream == 0) return;
        Bass.ChannelPlay(_stream);
        _isPlaying = true;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_stream == 0) return;
        Bass.ChannelPause(_stream);
        _isPlaying = false;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (_stream == 0) return;
        Bass.ChannelStop(_stream);
        Bass.ChannelSetPosition(_stream, 0, PositionFlags.Bytes);
        _isPlaying = false;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        if (_stream == 0) return;
        Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, position.TotalSeconds), PositionFlags.Bytes);
    }

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        if (_stream != 0) Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, (float)_volume);
    }

    public void SetMode(PlaybackMode mode)
    {
        _mode = mode;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEqualizerEnabled(bool enabled)
    {
        _eqEnabled = enabled;
        if (_stream != 0)
        {
            if (enabled) ApplyEqToStream();
            else FreeEqFx();
        }
    }

    public void SetEqualizerBand(int band, double gainDb)
    {
        if (band < 0 || band >= EqBands.Length) return;
        _eqGains[band] = Math.Clamp(gainDb, -12, 12);
        if (_eqEnabled && _eqFx is not null)
        {
            var bandIndex = _eqBandIndices[band];
            _eqFx.UpdateBand(bandIndex, _eqGains[band]);
        }
    }

    private void ApplyEqToStream()
    {
        FreeEqFx();
        if (_stream == 0 || !_eqEnabled) return;
        try
        {
            _eqFx = new DXParamEQ(_stream, 0);
            _eqBandIndices = new int[EqBands.Length];
            for (var i = 0; i < EqBands.Length; i++)
            {
                _eqBandIndices[i] = _eqFx.AddBand(EqBands[i]);
                if (_eqGains[i] != 0)
                    _eqFx.UpdateBand(_eqBandIndices[i], _eqGains[i]);
            }
        }
        catch
        {
            FreeEqFx();
        }
    }

    private void FreeEqFx()
    {
        if (_eqFx is not null)
        {
            _eqFx.Dispose();
            _eqFx = null;
        }
        _eqBandIndices = Array.Empty<int>();
    }

    public void Dispose() => FreeStream();

    /* ===================== visualizer data ===================== */

    public float[] GetSpectrumData(int bands = 64)
    {
        if (_stream == 0 || !_isPlaying) return Array.Empty<float>();

        // FFT512 → 256 complex bins, raw buffer length 1024 floats
        var fftBuffer = new float[1024];
        int got = Bass.ChannelGetData(_stream, fftBuffer, (int)DataFlags.FFT512);
        if (got <= 0) return Array.Empty<float>();

        // Convert complex bins to linear magnitudes (256 values)
        var magnitude = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float re = fftBuffer[i * 2];
            float im = fftBuffer[i * 2 + 1];
            magnitude[i] = (float)Math.Sqrt(re * re + im * im);
        }

        // Aggregate to target bands using logarithmic spacing (bass-dense)
        var result = new float[bands];
        int totalBins = 256;
        // Log-spaced boundaries from bin 2 to bin 255 (skip DC + Nyquist)
        for (int b = 0; b < bands; b++)
        {
            float t0 = (float)b / bands;
            float t1 = (float)(b + 1) / bands;
            // log curve: concentrate low frequencies
            int start = 2 + (int)Math.Round(Math.Pow(t0, 1.6) * (totalBins - 4));
            int end = 2 + (int)Math.Round(Math.Pow(t1, 1.6) * (totalBins - 4));
            if (end <= start) end = start + 1;
            float sum = 0;
            int count = 0;
            for (int i = start; i < end && i < totalBins; i++)
            {
                // Convert to dB, then clamp
                float db = 20f * (float)Math.Log10(magnitude[i] + 1e-10);
                // Map -80dB..0dB to 0..1
                sum += Math.Clamp((db + 80f) / 80f, 0f, 1f);
                count++;
            }
            result[b] = count > 0 ? sum / count : 0f;
        }

        // EMA smoothing
        _spectrumSmooth ??= new float[bands];
        const float alpha = 0.35f;
        for (int i = 0; i < bands; i++)
            result[i] = result[i] * alpha + _spectrumSmooth[i] * (1 - alpha);
        _spectrumSmooth = result;

        return result;
    }

    public float[] GetWaveformData(int samples = 512)
    {
        if (_stream == 0 || !_isPlaying) return Array.Empty<float>();

        lock (_dspLock)
        {
            if (_dspCount == 0) return Array.Empty<float>();

            int take = Math.Min(samples, _dspCount);
            int srcStart = _dspCount - take;
            var raw = new float[samples];
            for (int i = 0; i < take; i++)
                raw[i] = _dspMono[srcStart + i];

            // EMA smoothing (lighter). Guard against NaN/Inf leaking into the display data.
            _waveformSmooth ??= new float[samples];
            const float alpha = 0.6f;
            for (int i = 0; i < samples; i++)
            {
                float v = float.IsFinite(raw[i]) ? Math.Clamp(raw[i], -1f, 1f) : 0f;
                raw[i] = v * alpha + _waveformSmooth[i] * (1 - alpha);
            }
            _waveformSmooth = raw;

            return raw;
        }
    }

    /// <summary>
    /// BASS raw PCM cannot be pulled from a device-playback stream via ChannelGetData.
    /// A DSP guarantees waveform data arrives in the DSP callback (post-EQ, pre-output).
    /// </summary>
    private void AttachDspCapture()
    {
        DetachDspCapture();
        if (_stream == 0) return;
        _dspCaptureProc = OnDspCapture;
        _dspCaptureHandle = Bass.ChannelSetDSP(_stream, _dspCaptureProc, IntPtr.Zero, 0);
    }

    private void DetachDspCapture()
    {
        if (_dspCaptureHandle != 0)
        {
            if (_stream != 0) Bass.ChannelRemoveDSP(_stream, _dspCaptureHandle);
            _dspCaptureHandle = 0;
        }
        _dspCaptureProc = null;
        lock (_dspLock) _dspCount = 0;
    }

    private unsafe void OnDspCapture(int handle, int channel, IntPtr dspBuffer, int length, IntPtr user)
    {
        if (length < 8) return;

        Bass.ChannelGetInfo(channel, out var info);
        int ch = Math.Max(1, info.Channels);
        int frames = length / (4 * ch); // floats per channel → mono frames
        if (frames <= 0) return;

        var src = (float*)dspBuffer;
        lock (_dspLock)
        {
            // Large device buffers are clamped to the tail so the recent audio is kept.
            int copy = Math.Min(frames, DspMaxFrames);
            int frameOffset = frames - copy;
            int srcBase = frameOffset * ch;
            if (ch >= 2)
                for (int i = 0; i < copy; i++)
                {
                    float s = (src[srcBase + i * 2] + src[srcBase + i * 2 + 1]) * 0.5f;
                    _dspMono[i] = float.IsFinite(s) ? Math.Clamp(s, -1f, 1f) : 0f;
                }
            else
                for (int i = 0; i < copy; i++)
                {
                    float s = src[srcBase + i];
                    _dspMono[i] = float.IsFinite(s) ? Math.Clamp(s, -1f, 1f) : 0f;
                }
            _dspCount = copy;
        }
    }

    private void OnEnded(int handle, int channel, int data, IntPtr user)
    {
        _isPlaying = false;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void FreeStream()
    {
        FreeEqFx();
        DetachDspCapture();
        if (_stream != 0)
        {
            Bass.ChannelStop(_stream);
            Bass.StreamFree(_stream);
            _stream = 0;
        }
        _duration = TimeSpan.Zero;
    }
}
