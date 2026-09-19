namespace Xstar.Music.Core;

/// <summary>Abstraction over an online music source (search, streaming and download).</summary>
public interface IOnlineMusicProvider
{
    /// <summary>Stable machine key used to route a track back to its provider.</summary>
    string Key { get; }

    /// <summary>Human readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>False when the provider still needs configuration (e.g. a missing API key).</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Resolves a playable stream URL for the track. Returns null when unavailable.</summary>
    Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default);

    /// <summary>Downloads the track into <paramref name="directory"/> and returns the written file path.</summary>
    Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default);
}
