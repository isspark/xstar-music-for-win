namespace Xstar.Music.Core;

internal static class OnlineFileHelper
{
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "track" : result;
    }

    /// <summary>Derives a file extension from the content type or the URL, defaulting to .mp3.</summary>
    public static string ExtensionFor(string? url, string? contentType)
    {
        var ext = FromContentType(contentType);
        if (ext is not null) return ext;
        if (url is not null)
        {
            var path = url;
            var query = path.IndexOf('?');
            if (query >= 0) path = path[..query];
            var dot = path.LastIndexOf('.');
            if (dot >= 0 && dot < path.Length - 1)
            {
                var candidate = path[(dot + 1)..].ToLowerInvariant();
                if (candidate.Length <= 4 && candidate.All(char.IsLetterOrDigit)) return "." + candidate;
            }
        }
        return ".mp3";
    }

    public static async Task<string> SaveAsync(Stream source, string directory, string baseName, string? url, string? contentType, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Sanitize(baseName) + ExtensionFor(url, contentType));
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string? FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var value = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return value switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
            "audio/ogg" or "application/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            _ => null
        };
    }
}
