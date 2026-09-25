using System.Text.Json;

namespace Xstar.Music.App.ThreeD;

/// <summary>Shared JSON options for the C# &lt;-&gt; web bridge (camelCase, case-insensitive).</summary>
public static class ThreeDJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Playback state snapshot pushed to the 3D web view.</summary>
public sealed class ThreeDStateMessage
{
    public string Type => "state";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public long DurationMs { get; init; }
    public long PositionMs { get; init; }
    public bool Playing { get; init; }
    public string Accent { get; init; } = "#22C55E";
    public double Volume { get; init; }
    public bool Light { get; init; }
}

/// <summary>Per-frame audio features pushed to the 3D web view (~30 fps).</summary>
public sealed class ThreeDAudioMessage
{
    public string Type => "audio";
    public float[] Spectrum { get; init; } = Array.Empty<float>();
    public float Bass { get; init; }
    public float Mid { get; init; }
    public float Treble { get; init; }
    public bool Beat { get; init; }
    public double DtMs { get; init; }
}

/// <summary>Camera orbit / zoom delta injected by the host (empty-desktop drag on the wallpaper).</summary>
public sealed class ThreeDOrbitMessage
{
    public string Type => "orbit";
    public int Dx { get; init; }
    public int Dy { get; init; }
    public int Wheel { get; init; }
}

/// <summary>Album artwork data URI, pushed only when the current cover changes (or on handshake).</summary>
public sealed class ThreeDArtworkMessage
{
    public string Type => "artwork";
    public string? Key { get; init; }
    public string? Data { get; init; }
}

/// <summary>Generic library payload (albums / playlists / recent) pushed on request.</summary>
public sealed class ThreeDLibraryMessage
{
    public string Type => "library";
    public string Kind { get; init; } = "albums";
    public IReadOnlyList<ThreeDCard> Cards { get; init; } = Array.Empty<ThreeDCard>();
}

public sealed class ThreeDCard
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Kind { get; init; } = "collection";
    public int Count { get; init; }
}

/// <summary>Playlist / album detail (track list) pushed on request.</summary>
public sealed class ThreeDDetailMessage
{
    public string Type => "detail";
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public IReadOnlyList<ThreeDTrack> Tracks { get; init; } = Array.Empty<ThreeDTrack>();
}

public sealed class ThreeDTrack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public long DurationMs { get; init; }
    public int Index { get; init; }
}

/// <summary>Full lyric line list, pushed when the current song's lyrics change.</summary>
public sealed class ThreeDLyricsMessage
{
    public string Type => "lyrics";
    public IReadOnlyList<ThreeDLyricLine> Lines { get; init; } = Array.Empty<ThreeDLyricLine>();
}

public sealed class ThreeDLyricLine
{
    public long TimeMs { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>Active lyric line index, pushed whenever it changes.</summary>
public sealed class ThreeDLyricIndexMessage
{
    public string Type => "lyricIndex";
    public int Index { get; init; }
}

/// <summary>All persisted 3D preferences, pushed on handshake so the page can restore its UI.</summary>
public sealed class ThreeDSettingsMessage
{
    public string Type => "settings";
    public string Quality { get; init; } = "balanced";
    public string ShelfMode { get; init; } = "side";
    public string ShelfPresence { get; init; } = "auto";
    public string CameraMode { get; init; } = "dynamic";
    public string LibraryKind { get; init; } = "albums";
    public int CoverPreset { get; init; }
    public bool Bloom { get; init; } = false;
    public bool Lyrics { get; init; } = true;
    /// <summary>Lyric rendering: "default" (3D text stage) or "particle" (current line shapes the cloud).</summary>
    public string LyricMode { get; init; } = "default";
    public bool ReduceMotion { get; init; }
    public double ShelfSize { get; init; } = 1.0;
    public double ShelfOffsetX { get; init; }
    public double ShelfOffsetY { get; init; }
    public double ShelfAngleY { get; init; }
    public double ShelfOpacity { get; init; } = 1.0;
    public string BackgroundColor { get; init; } = "#05060A";
    public string? BackgroundImage { get; init; }
    public double ParticleSize { get; init; } = 1.0;
    public double Glow { get; init; } = 1.0;
    public double ParticleCount { get; init; } = 1.0;
    public int ParticleColorMode { get; init; } = 1;
    public string? ParticleColor { get; init; }
    /// <summary>When true the particle cloud is shaped by the album cover instead of title/lyrics text.</summary>
    public bool CoverParticles { get; init; }
}

/// <summary>Message sent by the web view to C# (action dispatch).</summary>
public sealed class ThreeDActionMessage
{
    public string? Type { get; set; }
    public string? Action { get; set; }
    public string? Mode { get; set; }
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public string? Key { get; set; }
    public string? Message { get; set; }
    public string? Edge { get; set; }
    public long? PositionMs { get; set; }
    public double? Volume { get; set; }
    public JsonElement? Value { get; set; }
}
