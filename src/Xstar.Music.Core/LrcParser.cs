using System.Globalization;
using System.Text.RegularExpressions;

namespace Xstar.Music.Core;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var result = new List<LyricLine>();
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = TimestampRegex().Matches(line);
            if (matches.Count == 0) continue;
            var lyric = TimestampRegex().Replace(line, "").Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
                var fractionMs = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    _ => int.Parse(fraction[..Math.Min(3, fraction.Length)], CultureInfo.InvariantCulture)
                };
                result.Add(new LyricLine((minutes * 60L + seconds) * 1000 + fractionMs, lyric));
            }
        }
        return result.OrderBy(x => x.TimeMs).ToArray();
    }
}
