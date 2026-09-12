using System.Net.Http;
using System.Text.Json;

namespace Xstar.Music.Core;

public sealed class OnlineLyricsProvider
{
    private const string SearchEndpoint = "https://lrclib.net/api/search";
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;

    public OnlineLyricsProvider(string cacheDirectory, HttpMessageHandler? handler = null)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XstarMusic/1.0");
    }

    public string CachePath(string songId) => Path.Combine(_cacheDirectory, songId + ".lrc");

    public bool HasCache(string songId) => File.Exists(CachePath(songId));

    public IReadOnlyList<LyricLine> ReadCache(string songId)
    {
        var path = CachePath(songId);
        return File.Exists(path) ? LrcParser.Parse(File.ReadAllText(path)) : Array.Empty<LyricLine>();
    }

    public async Task<IReadOnlyList<LyricLine>> FetchAsync(Song song, CancellationToken cancellationToken = default)
    {
        var cached = ReadCache(song.Id);
        if (cached.Count > 0) return cached;

        var url = $"{SearchEndpoint}?track_name={Uri.EscapeDataString(song.Title)}&artist_name={Uri.EscapeDataString(song.Artist)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return Array.Empty<LyricLine>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<LyricLine>();

        var targetSeconds = song.DurationMs / 1000.0;
        JsonElement? bestSynced = null;
        JsonElement? bestPlain = null;
        var bestScore = double.MaxValue;
        foreach (var candidate in document.RootElement.EnumerateArray())
        {
            var synced = GetString(candidate, "syncedLyrics");
            var plain = GetString(candidate, "plainLyrics");
            if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain)) continue;
            var duration = candidate.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var score = targetSeconds > 0 && duration > 0 ? Math.Abs(duration - targetSeconds) : 0;
            if (score >= bestScore) continue;
            bestScore = score;
            bestSynced = string.IsNullOrWhiteSpace(synced) ? null : candidate;
            bestPlain = string.IsNullOrWhiteSpace(plain) ? null : candidate;
            if (score < 2.5 && synced is not null) break;
        }

        if (bestSynced is not null)
        {
            var synced = GetString(bestSynced.Value, "syncedLyrics")!;
            var lines = LrcParser.Parse(synced);
            if (lines.Count > 0)
            {
                try { File.WriteAllText(CachePath(song.Id), synced); } catch { }
                return lines;
            }
        }
        if (bestPlain is not null)
        {
            var plain = GetString(bestPlain.Value, "plainLyrics")!;
            return plain.Split('\n').Select(line => new LyricLine(0, line.Trim())).Where(line => line.Text.Length > 0).ToList();
        }
        return Array.Empty<LyricLine>();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
