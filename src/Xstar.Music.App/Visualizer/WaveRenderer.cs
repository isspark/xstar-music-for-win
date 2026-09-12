using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

public sealed class WaveRenderer : IVisualizerRenderer
{
    public string StyleId => "wave";
    public string DisplayName => "波形";
    public bool IsHardwareCompatible => true;

    private WriteableBitmap? _bmp;
    private float _bassBoost;
    private float[]? _prev;

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _bassBoost = 0;
        _prev = null;
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;

        _bmp.Lock();
        try
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;

            var bg = frame.Background;
            RendererUtils.ClearLocked(_bmp, bg);

            var waveform = frame.Waveform;
            if (waveform.Length == 0) return;

            int midY = h / 2;
            _bassBoost = Math.Max(frame.BassEnergy, _bassBoost * 0.98f);
            float amplitude = (float)(h * 0.36) * (0.7f + _bassBoost * 0.6f + (frame.BeatDetected ? 0.1f : 0f));

            var (baseHue, sat, _) = RendererUtils.RgbToHsl(frame.Accent);
            var cTop = RendererUtils.HslToRgb(baseHue, sat, 0.95);
            var cFill = RendererUtils.HslToRgb((baseHue + 30) % 360, sat * 0.7, 0.6);

            int step = Math.Max(1, waveform.Length / w);
            _prev ??= new float[w];
            int[] ys = new int[w];

            // Collect amplitudes and draw the (mirrored) glowing line.
            for (int x = 0; x < w; x++)
            {
                int idx = x * step;
                if (idx >= waveform.Length) break;
                float sample = Math.Clamp(waveform[idx], -1f, 1f);
                sample = sample * 0.6f + _prev[x] * 0.4f;
                _prev[x] = sample;

                int y = midY - (int)(sample * amplitude);
                ys[x] = y;

                // Glowing point on the curve
                RendererUtils.GlowLocked(_bmp, x, y, 3.2f, cFill.R, cFill.G, cFill.B, 0.9f);
            }

            // Connect the points into a continuous line (upper half)
            for (int x = 1; x < w; x++)
            {
                int y0 = ys[x - 1], y1 = ys[x];
                DrawLine(_bmp, x - 1, y0, x, y1, cTop, 0.85f);
            }

            // Mirror reflection below the centre line
            int reflBottom = midY + (int)((h - midY) * 0.4);
            unsafe
            {
                for (int y0 = midY; y0 < reflBottom; y0++)
                {
                    int srcY = midY - (y0 - midY);
                    if (srcY < 0) break;
                    float fade = 1f - (float)(y0 - midY) / Math.Max(1, reflBottom - midY);
                    for (int x = 0; x < w; x += 2)
                    {
                        byte* dst = Px(_bmp, x, y0);
                        byte* src = Px(_bmp, x, srcY);
                        float k = fade * 0.4f;
                        dst[0] = (byte)(src[0] * k + bg.B * (1f - k));
                        dst[1] = (byte)(src[1] * k + bg.G * (1f - k));
                        dst[2] = (byte)(src[2] * k + bg.R * (1f - k));
                        dst[3] = 255;
                    }
                }
            }

            // Luminous centre baseline
            var baseHot = RendererUtils.HslToRgb(baseHue, sat, 0.9);
            for (int x = 0; x < w; x += 2)
                RendererUtils.GlowLocked(_bmp, x, midY, 2.5f, baseHot.R, baseHot.G, baseHot.B, 0.35f);
        }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    private static unsafe void DrawLine(WriteableBitmap bmp, int x0, int y0, int x1, int y1, Color c, float alpha)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, x = x0, y = y0;
        for (; ; )
        {
            if (x >= 0 && x < bmp.PixelWidth && y >= 0 && y < bmp.PixelHeight)
            {
                byte* px = Px(bmp, x, y);
                px[0] = (byte)Math.Min(255, (int)(px[0] + c.B * alpha));
                px[1] = (byte)Math.Min(255, (int)(px[1] + c.G * alpha));
                px[2] = (byte)Math.Min(255, (int)(px[2] + c.R * alpha));
            }
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    private static unsafe byte* Px(WriteableBitmap bmp, int x, int y)
    {
        return (byte*)bmp.BackBuffer + y * bmp.BackBufferStride + x * 4;
    }

    public void Cleanup() { _bmp = null; }
}