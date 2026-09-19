using System.Net.Http;
using System.Text.Json;

namespace Xstar.Music.Core;

/// <summary>Audius (api.audius.co) provider. Opens the largest permissionless music catalog.</summary>
public sealed class AudiusProvider : IOnlineMusicProvider
{
    private const string BaseEndpoint = "https://api.audius.co/v1";
    private readonly HttpClient _http;

    public AudiusProvider(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? OnlineHttp.Create(TimeSpan.FromSeconds(12))
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XstarMusic/1.0");
    }

    public string Key => "audius";

    public string DisplayName => "Audius";

    /// <summary>Identifies the application to Audius; sent as the app_name query parameter.</summary>
    public string AppName { get; set; } = "XstarMusic";

    /// <summary>Optional API key. Read-only access works without one today.</summary>
    public string ApiKey { get; set; } = "";

    public bool IsConfigured => true;

    public async Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<OnlineTrack>();

        var url = $"{BaseEndpoint}/tracks/search?query={Uri.EscapeDataString(query)}&limit=30&app_name={Uri.EscapeDataString(AppName)}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document is null) return Array.Empty<OnlineTrack>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var tracks = new List<OnlineTrack>();
        foreach (var item in data.EnumerateArray())
        {
            var id = GetString(item, "id");
            var title = GetString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

            // Skip tracks that the network cannot stream (gated/deleted); they would fail on playback.
            if (item.TryGetProperty("is_streamable", out var streamable) && streamable.ValueKind == JsonValueKind.False) continue;
            if (item.TryGetProperty("is_delete", out var deleted) && deleted.ValueKind == JsonValueKind.True) continue;
            if (item.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.False) continue;

            var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
            var downloadable = item.TryGetProperty("is_downloadable", out var dl) && dl.ValueKind == JsonValueKind.True;

            tracks.Add(new OnlineTrack(
                id,
                title,
                GetNestedString(item, "user", "name") ?? "未知艺术家",
                GetString(item, "album") ?? GetString(item, "album_name") ?? "",
                duration * 1000,
                StreamUrl(id),
                downloadable ? DownloadUrl(id) : null,
                GetArtwork(item))
            {
                ProviderKey = Key
            });
        }
        return tracks;
    }

    public Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default)
    {
        var url = track.StreamUrl;
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(track.Id)) url = StreamUrl(track.Id);
        return Task.FromResult(url);
    }

    public async Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default)
    {
        var url = track.DownloadUrl ?? track.StreamUrl ?? StreamUrl(track.Id);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await OnlineFileHelper.SaveAsync(stream, directory, $"{track.Artist} - {track.Title}", url, contentType, cancellationToken).ConfigureAwait(false);
    }

    private string StreamUrl(string id) => $"{BaseEndpoint}/tracks/{Uri.EscapeDataString(id)}/stream?app_name={Uri.EscapeDataString(AppName)}";

    private string DownloadUrl(string id) => $"{BaseEndpoint}/tracks/{Uri.EscapeDataString(id)}/download?app_name={Uri.EscapeDataString(AppName)}";

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(ApiKey)) request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
    }

    private static string? GetArtwork(JsonElement element)
    {
        if (!element.TryGetProperty("artwork", out var artwork) || artwork.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "480x480", "150x150", "1000x1000" })
            if (artwork.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string? GetNestedString(JsonElement element, string parent, string property)
    {
        if (!element.TryGetProperty(parent, out var node) || node.ValueKind != JsonValueKind.Object) return null;
        return GetString(node, property);
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}
