using System.Net.Http;
using System.Text.Json;

namespace Xstar.Music.Core;

/// <summary>Jamendo (api.jamendo.com) provider of free Creative Commons music.</summary>
public sealed class JamendoProvider : IOnlineMusicProvider
{
    private const string BaseEndpoint = "https://api.jamendo.com/v3.0";
    private readonly HttpClient _http;

    public JamendoProvider(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? OnlineHttp.Create(TimeSpan.FromSeconds(15))
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XstarMusic/1.0");
    }

    public string Key => "jamendo";

    public string DisplayName => "Jamendo";

    public string ClientId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public async Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(query)) return Array.Empty<OnlineTrack>();
        var url = $"{BaseEndpoint}/tracks/?client_id={Uri.EscapeDataString(ClientId)}&format=json&limit=30&search={Uri.EscapeDataString(query)}&audioformat=mp32";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return Array.Empty<OnlineTrack>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<OnlineTrack>();

        var tracks = new List<OnlineTrack>();
        foreach (var item in results.EnumerateArray())
        {
            var id = GetString(item, "id");
            var title = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
            var allowed = !item.TryGetProperty("audiodownload_allowed", out var a) || a.ValueKind != JsonValueKind.False;
            tracks.Add(new OnlineTrack(
                "jamendo:" + id,
                title,
                GetString(item, "artist_name") ?? "未知艺术家",
                GetString(item, "album_name") ?? "",
                duration * 1000,
                GetString(item, "audio"),
                allowed ? GetString(item, "audiodownload") : null,
                GetString(item, "image"))
            {
                ProviderKey = Key
            });
        }
        return tracks;
    }

    public Task<string?> ResolveStreamUrlAsync(OnlineTrack track, CancellationToken cancellationToken = default) =>
        Task.FromResult(track.StreamUrl);

    public async Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default)
    {
        var url = track.DownloadUrl ?? track.StreamUrl ?? throw new InvalidOperationException("该曲目不提供下载");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await OnlineFileHelper.SaveAsync(source, directory, $"{track.Artist} - {track.Title}", url, contentType, cancellationToken).ConfigureAwait(false);
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
