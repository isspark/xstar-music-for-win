namespace Xstar.Music.Core;

/// <summary>A single search result from an online music provider.</summary>
public sealed record OnlineTrack(
    string Id,
    string Title,
    string Artist,
    string Album,
    long DurationMs,
    string? StreamUrl,
    string? DownloadUrl,
    string? ImageUrl)
{
    /// <summary>Key of the provider that produced this track (e.g. "audius", "gdstudio", "jamendo").</summary>
    public string ProviderKey { get; init; } = "";

    /// <summary>True when the provider can produce a downloadable file, even if the URL is resolved lazily.</summary>
    public bool CanDownload { get; init; }

    public bool Downloadable => CanDownload || !string.IsNullOrWhiteSpace(DownloadUrl);
}
