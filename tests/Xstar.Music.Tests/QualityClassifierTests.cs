using Xstar.Music.Core;

namespace Xstar.Music.Tests;

public sealed class QualityClassifierTests
{
    [Theory]
    [InlineData("FLAC", 0, 44100, 16, "无损")]
    [InlineData("FLAC", 0, 96000, 24, "Hi-Res")]
    [InlineData("MP3", 320, 44100, 0, "高清")]
    [InlineData("MP3", 128, 44100, 0, "普通")]
    public void AppliesQualityRules(string format, int bitrate, int sampleRate, int bitDepth, string expected)
    {
        Assert.Equal(expected, QualityClassifier.Classify(format, bitrate, sampleRate, bitDepth));
    }
}
