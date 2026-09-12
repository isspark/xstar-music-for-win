namespace Xstar.Music.Core;

public static class QualityClassifier
{
    public static string Classify(string format, int bitrate, int sampleRate, int bitDepth)
    {
        if (bitDepth >= 24 || sampleRate > 48000) return "Hi-Res";
        if (format is "FLAC" or "ALAC" or "APE" or "WAV") return "无损";
        return bitrate >= 256 ? "高清" : "普通";
    }
}
