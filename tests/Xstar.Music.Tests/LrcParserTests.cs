using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void ParsesMultipleTimestampsAndUtf8Bom()
    {
        var lines = LrcParser.Parse("\uFEFF[00:01.20][00:02:300]Hello");

        Assert.Equal(2, lines.Count);
        Assert.Equal(1200, lines[0].TimeMs);
        Assert.Equal(2300, lines[1].TimeMs);
        Assert.All(lines, line => Assert.Equal("Hello", line.Text));
    }
}
