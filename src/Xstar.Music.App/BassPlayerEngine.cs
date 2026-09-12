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

    public void Load(Song song)
    {
        FreeStream();
        _song = song;
        _stream = Bass.CreateStream(song.Path, 0, 0, BassFlags.Default);
        if (_stream == 0)
        {
            Failed?.Invoke(this, $"无法打开文件（{Bass.LastError}）");
            return;
        }
        var length = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
        var seconds = length > 0 ? Bass.ChannelBytes2Seconds(_stream, length) : 0;
        _duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, (float)_volume);
        ApplyEqToStream();
        _isPlaying = false;
        Opened?.Invoke(this, EventArgs.Empty);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

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

    private void OnEnded(int handle, int channel, int data, IntPtr user)
    {
        _isPlaying = false;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void FreeStream()
    {
        FreeEqFx();
        if (_stream != 0)
        {
            Bass.ChannelStop(_stream);
            Bass.StreamFree(_stream);
            _stream = 0;
        }
        _duration = TimeSpan.Zero;
    }
}
