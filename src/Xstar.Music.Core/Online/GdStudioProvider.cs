using System.Net.Http;
using System.Text.Json;

namespace Xstar.Music.Core;

/// <summary>
/// GD 音乐台 (music-api.gdstudio.xyz) provider. Aggregates Chinese and international
/// music sources. Search returns metadata; the actual playable URL is resolved on demand.
/// </summary>
public sealed class GdStudioProvider : IOnlineMusicProvider
{
    // Primary host plus the historical mirror; the first one that answers is used.
    private static readonly string[] Endpoints =
    {
        "https://music-api.gdstudio.xyz/api.php",
        "https://music.gdstudio.xyz/api.php"
    };

    private readonly HttpClient _http;

    public GdStudioProvider(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? OnlineHttp.Create(TimeSpan.FromSeconds(12))
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) XstarMusic/1.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.gdstudio.xyz/");
    }

    public string Key => "gdstudio";

    public string DisplayName => "GD音乐台";

    /// <summary>Underlying catalog: netease, tencent, kuwo, kugou, migu, tidal, spotify, ...</summary>
    public string Source { get; set; } = "netease";

    /// <summary>128 / 192 / 320 / 740 (lossless) / 999 (hi-res).</summary>
    public int Bitrate { get; set; } = 320;

    public bool IsConfigured => true;

    public async Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<OnlineTrack>();

        var parameters = new Dictionary<string, string>
        {
            ["types"] = "search",
            ["source"] = Source,
            ["name"] = query,
            ["count"] = "25",
            ["pages"] = "1"
        };

        using var document = await GetJsonAsync(parameters, cancellationToken).ConfigureAwait(false);

        if (!TryGetItems(document.RootElement, out var items)) return Array.Empty<OnlineTrack>();

        var tracks = new List<OnlineTrack>();
        foreach (var item in items.EnumerateArray())
        {
            var id = GetString(item, "id");
            var title = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

            tracks.Add(new OnlineTrack(
                $"{Source}|{id}",
                title,
                GetArtists(item) ?? "未知艺术家",
                GetString(item, "album") ?? "",
                0,
                null,
                null,
                null)
            {
                ProviderKey = Key,
                CanDownload = true
            });
        }
        return tracks;
    }

    public async Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default)
    {
        if (!TryParseId(track.Id, out var source, out var id)) return null;

        var parameters = new Dictionary<string, string>
        {
            ["types"] = "url",
            ["source"] = source,
            ["id"] = id,
            ["br"] = Bitrate.ToString()
        };

        try
        {
            using var document = await GetJsonAsync(parameters, cancellationToken).ConfigureAwait(false);
            var url = GetString(document.RootElement, "url");
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public async Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default)
    {
        var url = await ResolveStreamUrlAsync(track, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("该曲目暂时无法获取播放地址");

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await OnlineFileHelper.SaveAsync(stream, directory, $"{track.Artist} - {track.Title}", url, contentType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var endpoint in Endpoints)
        {
            var url = endpoint + "?" + string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0) return JsonDocument.Parse("[]");
                return JsonDocument.Parse(bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; /* try the next endpoint */ }
        }
        throw new HttpRequestException("GD音乐台当前无法访问，请稍后重试或切换音源", lastError);
    }

    private static bool TryParseId(string raw, out string source, out string id)
    {
        source = "";
        id = "";
        var separator = raw.IndexOf('|');
        if (separator <= 0 || separator >= raw.Length - 1) return false;
        source = raw[..separator];
        id = raw[(separator + 1)..];
        return true;
    }

    private static bool TryGetItems(JsonElement root, out JsonElement items)
    {
        if (root.ValueKind == JsonValueKind.Array) { items = root; return true; }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            items = data;
            return true;
        }
        items = default;
        return false;
    }

    private static string? GetArtists(JsonElement element)
    {
        if (!element.TryGetProperty("artist", out var artists)) return null;
        if (artists.ValueKind == JsonValueKind.String) return artists.GetString();
        if (artists.ValueKind != JsonValueKind.Array) return null;
        var names = artists.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join(" / ", names);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
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
