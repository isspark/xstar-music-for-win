using System.Net.Http;
using System.Text.Json;

namespace Xstar.Music.Core;

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
    public bool Downloadable => !string.IsNullOrWhiteSpace(DownloadUrl);
}

public sealed class JamendoProvider
{
    private const string BaseEndpoint = "https://api.jamendo.com/v3.0";
    private readonly HttpClient _http;

    public JamendoProvider(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XstarMusic/1.0");
    }

    public string ClientId { get; set; } = "";

    public async Task<IReadOnlyList<OnlineTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(query)) return Array.Empty<OnlineTrack>();
        var url = $"{BaseEndpoint}/tracks/?client_id={Uri.EscapeDataString(ClientId)}&format=json&limit=30&search={Uri.EscapeDataString(query)}&audioformat=mp32";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return Array.Empty<OnlineTrack>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
                GetString(item, "image")));
        }
        return tracks;
    }

    public async Task<string> DownloadAsync(OnlineTrack track, string directory, CancellationToken cancellationToken = default)
    {
        var url = track.DownloadUrl ?? track.StreamUrl ?? throw new InvalidOperationException("该曲目不提供下载");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Sanitize($"{track.Artist} - {track.Title}") + ".mp3");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
