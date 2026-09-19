using System.Net.Http;

namespace Xstar.Music.Core;

/// <summary>Aggregates the configured online music providers and routes track operations back to their origin.</summary>
public sealed class OnlineMusicService
{
    public const string AllProviders = "all";

    /// <summary>Max time a single provider may take before the merged search gives up on it.</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, IOnlineMusicProvider> _providers;

    public OnlineMusicService(IEnumerable<IOnlineMusicProvider> providers)
    {
        Providers = providers.ToList();
        _providers = Providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IOnlineMusicProvider> Providers { get; }

    public IOnlineMusicProvider? Find(string key) =>
        !string.IsNullOrWhiteSpace(key) && _providers.TryGetValue(key, out var provider) ? provider : null;

    /// <summary>Searches one provider, or every configured provider when <paramref name="providerKey"/> is "all".</summary>
    public async Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, string providerKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<OnlineTrack>();

        if (!string.IsNullOrWhiteSpace(providerKey) && providerKey != AllProviders)
        {
            var provider = Find(providerKey);
            if (provider is null) return Array.Empty<OnlineTrack>();
            return await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var targets = Providers.Where(p => p.IsConfigured).ToList();
        if (targets.Count == 0) return Array.Empty<OnlineTrack>();

        var tasks = targets.Select(async p =>
        {
            try
            {
                return await p.SearchAsync(query, cancellationToken)
                    .WaitAsync(ProviderTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return (IReadOnlyList<OnlineTrack>)Array.Empty<OnlineTrack>(); }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    public async Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default)
    {
        var provider = Find(track.ProviderKey);
        if (provider is null) return track.StreamUrl;
        return await provider.ResolveStreamUrlAsync(track, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default)
    {
        var provider = Find(track.ProviderKey) ?? throw new InvalidOperationException($"未知的在线音源：{track.ProviderKey}");
        return provider.DownloadAsync(track, directory, cancellationToken);
    }
}
