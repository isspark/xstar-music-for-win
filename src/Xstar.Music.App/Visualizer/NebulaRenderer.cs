using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Xstar.Music.App.Visualizer;

/// <summary>
/// 星云·风烟：粗网格半拉格朗日烟雾模拟。密度场被一张随时间缓慢演化的
/// 散度自由风场（流函数叠加）吹动、拉伸、翻卷，配合多个缓慢游移的发烟点，
/// 呈现类似“风吹烟”般持续流变、舒卷的半透明烟幕。低频驱动阵风与烟量，
/// 鼓点喷出烟团并赋予径向冲击，高频增强小尺度颤动。
/// 合成阶段以密度梯度＋拉普拉斯提取烟幕折叠处的纤细亮褶，微染主题色，
/// 整体为近黑白灰的风烟影调，背景仍使用主题派生底色。
/// </summary>
public sealed class NebulaRenderer : IVisualizerRenderer
{
    public string StyleId => "nebula";
    public string DisplayName => "星云";
    public bool IsHardwareCompatible => true;

    // 模拟网格（独立于位图分辨率，合成时双线性放大）
    private const int GW = 240;
    private const int GH = 180;
    private const float MaxDt = 0.05f;

    private WriteableBitmap? _bmp;

    private float[] _den = null!;   // 烟雾密度
    private float[] _denTmp = null!;
    private float[] _vx = null!;    // 速度场（格/秒）
    private float[] _vy = null!;
    private float[] _vxTmp = null!;
    private float[] _vyTmp = null!;
    private float[] _edge = null!;  // 褶皱强度场
    // 目标风场在半分辨率网格上预算（风场平滑，三角函数成本降 3/4），牵引时双线性采样
    private const int TW = GW / 2, TH = GH / 2;
    private float[] _windX = null!;
    private float[] _windY = null!;

    private readonly Random _rng = new(11);
    private double _time;
    private double _windClock;   // 风场相位时钟：鼓点/高频会“刮快一阵”
    private float _beat;
    private int _beatEmitter;
    private uint _grainSeed = 1;

    // 发烟点：全部为竖直窄缝，像几缕烟柱自底部/左缘升起，被风摇摆翻卷。
    // 归一化位置(U 横向, V 纵向, 顶=0)、椭圆半径 RX/RY(格)、绑定频段、相位
    private readonly Emitter[] _emitters =
    {
        new(0.110f, 0.985f, 4,  9, 0.04f, 0.3f),
        new(0.330f, 0.972f, 3,  8, 0.12f, 2.2f),
        new(0.560f, 0.990f, 3,  8, 0.27f, 4.0f),
        new(0.015f, 0.660f, 3, 12, 0.07f, 4.8f),
        new(0.045f, 0.300f, 3, 13, 0.12f, 5.4f),
    };

    private struct Emitter
    {
        public readonly float U, V, Bin, Phase;
        public readonly int RX, RY;
        public Emitter(float u, float v, int rx, int ry, float bin, float phase)
        { U = u; V = v; RX = rx; RY = ry; Bin = bin; Phase = phase; }
    }

    public void Initialize(WriteableBitmap bitmap)
    {
        _bmp = bitmap;
        _time = 0;
        _windClock = 0;
        _beat = 0;
        _beatEmitter = 0;
        _grainSeed = 1;

        int cells = GW * GH;
        _den = new float[cells];
        _denTmp = new float[cells];
        _vx = new float[cells];
        _vy = new float[cells];
        _vxTmp = new float[cells];
        _vyTmp = new float[cells];
        _edge = new float[cells];
        _windX = new float[TW * TH];
        _windY = new float[TW * TH];

        // 预热：先演化约 17 秒，让进入星云时烟已升起、翻卷成幕
        const float warmDt = 1f / 30f;
        for (int i = 0; i < 520; i++)
        {
            _time += warmDt;
            Step(warmDt, 0.03f, 0.10f, 0f, 0f, ReadOnlySpan<float>.Empty, 0);
        }

        bitmap.Lock();
        try { RendererUtils.ClearLocked(bitmap, RendererUtils.DefaultBackground); }
        finally { RendererUtils.FlushLocked(bitmap); }
    }

    public void Render(VisualizerFrame frame)
    {
        if (_bmp is null) return;

        float dt = Math.Clamp((float)frame.DeltaTimeMs / 1000f, 1f / 120f, MaxDt);
        float bass = Math.Clamp(frame.BassEnergy, 0f, 1f);
        float mid = Math.Clamp(frame.MidEnergy, 0f, 1f);
        float treble = Math.Clamp(frame.TrebleEnergy, 0f, 1f);

        _time += dt;
        _grainSeed = (uint)(_time * 91.7) * 2654435761u + 13579u;

        if (frame.BeatDetected)
        {
            _beat = 1f;
            _beatEmitter = (_beatEmitter + 1) % _emitters.Length;
        }
        else
        {
            _beat = Math.Max(0f, _beat - dt * 1.6f);
        }

        var spec = frame.Spectrum;
        int usable = spec.Length > 0 ? Math.Max(8, (int)(spec.Length * 0.7f)) : 0;

        Step(dt, bass, mid, treble, _beat, spec, usable);

        _bmp.Lock();
        try { Composite(frame); }
        finally { RendererUtils.FlushLocked(_bmp); }
    }

    // ── 模拟一步 ─────────────────────────────────────────────────────────

    private void Step(float dt, float bass, float mid, float treble, float beat,
        ReadOnlySpan<float> spec, int usable)
    {
        // 风场相位：鼓点/高频刮过一阵急风，烟会被猛地吹散后再缓慢聚拢
        _windClock += dt * (1.0 + beat * 2.4 + treble * 0.7);
        double t = _windClock;
        float tF = (float)t;

        // 整体阵风包络：缓慢的风力强弱起伏；低频与鼓点把风推强
        float gustEnv = 0.75f
            + 0.22f * MathF.Sin(tF * 0.45f)
            + 0.12f * MathF.Sin(tF * 0.16f + 1.7f);
        float gust = Math.Clamp(gustEnv * (1f + bass * 1.5f + beat * 0.9f), 0.25f, 3.2f);
        float flutter = 0.80f + treble * 1.6f + beat * 1.2f;

        // 1a) 目标风场预算到半分辨率网格：风场平滑，半网格 + 双线性采样几乎无损，
        //     却把每帧数万次三角函数降到 1/4。
        for (int ty = 0; ty < TH; ty++)
        {
            float v = (ty + 0.5f) / TH;
            int trow = ty * TW;
            for (int tx = 0; tx < TW; tx++)
            {
                Wind((tx + 0.5f) / TW, v, t, gust, flutter,
                    out float wx, out float wy);
                int ti = trow + tx;
                _windX[ti] = wx;
                _windY[ti] = wy;
            }
        }

        // 1b) 速度被风场牵引（指数趋近，天然稳定）；浓烟受浮力上升，风负责摇摆翻卷。
        //     牵引稍快，避免旧速度残留把烟折成直角。
        float follow = 1f - MathF.Exp(-dt * 2.4f);
        const float buoyancy = 7.0f;
        for (int y = 0; y < GH; y++)
        {
            float fv = (y + 0.5f) / GH * (TH - 1);
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                float fu = (x + 0.5f) / GW * (TW - 1);
                int i = row + x;
                float wx = Bilerp(_windX, TW, TH, fu, fv);
                float wy = Bilerp(_windY, TW, TH, fu, fv);
                _vx[i] += (wx - _vx[i]) * follow;
                _vy[i] += (wy - _vy[i]) * follow;
                _vy[i] -= buoyancy * Math.Clamp(_den[i] * 1.6f, 0f, 1f) * dt;
            }
        }

        // 2) 涡量约束（vorticity confinement）：粗网格会数值耗散掉小涡，
        //    显式把涡核周围的速度重新卷起来——烟柱边缘因此碎成烟丝与涡卷，
        //    而不是光滑的“蜡烛/熔岩”。_edge 在此刻空闲，用作涡量暂存。
        float vort = 4.2f * (0.75f + 0.45f * flutter);
        for (int y = 1; y < GH - 1; y++)
        {
            int row = y * GW;
            for (int x = 1; x < GW - 1; x++)
            {
                int i = row + x;
                _edge[i] = (_vy[i + 1] - _vy[i - 1]) * 0.5f
                         - (_vx[i + GW] - _vx[i - GW]) * 0.5f;
            }
        }
        for (int y = 1; y < GH - 1; y++)
        {
            int row = y * GW;
            for (int x = 1; x < GW - 1; x++)
            {
                int i = row + x;
                float ox = (MathF.Abs(_edge[i + 1]) - MathF.Abs(_edge[i - 1])) * 0.5f;
                float oy = (MathF.Abs(_edge[i + GW]) - MathF.Abs(_edge[i - GW])) * 0.5f;
                float invLen = 1f / (MathF.Sqrt(ox * ox + oy * oy) + 1e-4f);
                float nx = ox * invLen, ny = oy * invLen;
                float smoke = Math.Clamp(_den[i] * 1.6f, 0f, 1f);
                // N × ω（2D）：(Ny·ω, -Nx·ω)
                _vx[i] += ny * _edge[i] * vort * smoke * dt;
                _vy[i] += -nx * _edge[i] * vort * smoke * dt;
            }
        }

        // 3) 速度自平流（半拉格朗日回采）
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                int i = row + x;
                float px = x - _vx[i] * dt;
                float py = y - _vy[i] * dt;
                _vxTmp[i] = Bilerp(_vx, GW, GH, px, py) * 0.996f;
                _vyTmp[i] = Bilerp(_vy, GW, GH, px, py) * 0.996f;
            }
        }
        (_vx, _vxTmp) = (_vxTmp, _vx);
        (_vy, _vyTmp) = (_vyTmp, _vy);

        // 3) 速度轻度耗散平滑（抑制数值噪点，保持层流般的顺滑）
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                int i = row + x;
                if (x == 0 || y == 0 || x == GW - 1 || y == GH - 1)
                {
                    _vxTmp[i] = _vx[i];
                    _vyTmp[i] = _vy[i];
                    continue;
                }
                float ax = (_vx[i - 1] + _vx[i + 1] + _vx[i - GW] + _vx[i + GW]) * 0.25f;
                float ay = (_vy[i - 1] + _vy[i + 1] + _vy[i - GW] + _vy[i + GW]) * 0.25f;
                _vxTmp[i] = 0.96f * _vx[i] + 0.04f * ax;
                _vyTmp[i] = 0.96f * _vy[i] + 0.04f * ay;
            }
        }
        (_vx, _vxTmp) = (_vxTmp, _vx);
        (_vy, _vyTmp) = (_vyTmp, _vy);

        // 4) 密度平流：沿速度场回采上一时刻的烟。
        //    边界开放：采样点越出网格即视为 0（烟飘出屏幕消散），
        //    否则烟会在封闭边界堆积成“墙”，被棱光勾出水平亮带。
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                int i = row + x;
                float px = x - _vx[i] * dt;
                float py = y - _vy[i] * dt;
                _denTmp[i] = (px < -0.5f || px > GW - 0.5f
                           || py < -0.5f || py > GH - 0.5f)
                    ? 0f : Bilerp(_den, GW, GH, px, py);
            }
        }
        (_den, _denTmp) = (_denTmp, _den);

        // 5) 发烟点注入（含鼓点喷发与冲击）
        for (int e = 0; e < _emitters.Length; e++)
        {
            float band = 0f;
            if (usable > 0)
            {
                int bi = Math.Clamp((int)(_emitters[e].Bin * usable), 0, spec.Length - 1);
                band = spec[bi];
            }
            // 基量要足：安静段落也持续冒烟成幕，音乐只在其上加强
            float rate = 1.15f + mid * 1.40f + bass * 0.70f + band * 1.10f;
            float amount = 0.0125f * rate;
            float burst = (e == _beatEmitter) ? _beat : 0f;
            Inject(_emitters[e], amount, burst, t, gust);
        }

        // 6) 两轮极轻扩散：仅柔化颗粒，风切拉出的细丝尽量保留
        BoxBlur(_den, _denTmp, 0.025f);
        BoxBlur(_denTmp, _den, 0.012f);

        // 7) 耗散：只用纯比例衰减（绝对扣减会在长旅程中把薄烟全部抽干，
        //    烟活不到顶部）；越靠右消散越快，给画面留出黑色空域。
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                float keep = 0.9994f - 0.0012f * (x / (float)(GW - 1));
                int i = row + x;
                _den[i] *= keep;
            }
        }

        // 8) 褶皱场：一阶梯度（烟幕轮廓光）＋脊线（密度凸起折痕，-lap>0）
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                int i = row + x;
                int il = x > 0 ? i - 1 : i;
                int ir = x < GW - 1 ? i + 1 : i;
                int iu = y > 0 ? i - GW : i;
                int id = y < GH - 1 ? i + GW : i;
                float gx = _den[ir] - _den[il];
                float gy = _den[id] - _den[iu];
                float lap = _den[il] + _den[ir] + _den[iu] + _den[id] - 4f * _den[i];
                // 左上来光：只点亮迎光的棱面（左缘/上缘），避免对称双线勾边；
                // 少量全向梯度保留暗面层次，ridge 补充折叠隆起的高光。
                float lit = gx * 0.45f + gy * 0.85f;
                float ridge = -lap;
                float e = (lit > 0f ? lit * 2.20f : 0f)
                        + MathF.Sqrt(gx * gx + gy * gy) * 0.45f
                        + (ridge > 0f ? ridge * 0.80f : 0f);
                _edge[i] = float.IsFinite(e) ? e : 0f;
            }
        }
    }

    /// <summary>时间动画的散度自由风场：基底右吹气流＋三层流函数涡旋叠加。</summary>
    private static void Wind(float u, float v, double t, float gust, float flutter,
        out float wx, out float wy)
    {
        const double TAU = System.Math.PI * 2.0;

        float swx = 0f, swy = 0f;
        // 三层涡旋：整屏级蛇形摇摆 / 柱身翻卷 / 边缘小丝缕；
        // 相位速度使烟柱每隔几秒就有肉眼可见的流变。
        AddOctave(1.7f, 1.60f, 5.0f, 0.38, 0.28, 0.0, 1f);
        AddOctave(3.8f, 2.9f, 4.6f, -0.60, 0.45, 2.1, 0.85f);
        AddOctave(7.6f, 5.6f, 2.4f, 0.92, -0.78, 4.4, 0.35f + 0.30f * flutter);
        // 柱身尺度的高频涡：打碎光滑的蘑菇头，卷出烟丝与细碎褶
        AddOctave(15.0f, 11.5f, 3.2f, 1.40, -1.10, 3.3, 0.30f + 0.45f * flutter);
        // 柱宽尺度的微涡：撕出纤维丝缕
        AddOctave(24.0f, 18.0f, 2.0f, 2.20, -1.80, 5.1, 0.22f + 0.40f * flutter);

        float swirlScale = 0.70f + 0.50f * gust;
        wx = 0.8f * gust + swx * swirlScale;
        wy = swy * swirlScale;

        void AddOctave(float fx, float fy, float s, double w1, double w2, double ph, float amp)
        {
            double a = fx * TAU * u + t * w1 + ph;
            double b = fy * TAU * v + t * w2 + ph * 1.3;
            double sa = System.Math.Sin(a), ca = System.Math.Cos(a);
            double sb = System.Math.Sin(b), cb = System.Math.Cos(b);
            float inv = 1f / MathF.Sqrt(fx * fx + fy * fy);
            // ψ = S·sin(a)·cos(b)；w = (∂ψ/∂v, -∂ψ/∂u)
            swx += (float)(-s * fy * inv * sa * sb) * amp;
            swy += (float)(-s * fx * inv * ca * cb) * amp;
        }
    }

    private void Inject(Emitter e, float amount, float burst, double t, float gust)
    {
        // 发烟点自身缓慢游移
        float eu = (e.U
            + 0.035f * MathF.Sin((float)t * 0.13f + e.Phase)
            + 0.020f * MathF.Sin((float)t * 0.31f + e.Phase * 2f)) * GW;
        float ev = (e.V
            + 0.060f * MathF.Sin((float)t * 0.09f + e.Phase * 1.7f)) * GH;

        int cx = (int)MathF.Round(eu);
        int cy = (int)MathF.Round(ev);
        int rx = e.RX, ry = e.RY;
        float inv2sx2 = 1f / (2f * (rx * 0.72f) * (rx * 0.72f));
        float inv2sy2 = 1f / (2f * (ry * 0.72f) * (ry * 0.72f));
        // 源头刻意不饱和：半透烟体靠褶皱棱线显形，而非一团纯白。
        // 鼓点只比常态略浓（旧值 burst*0.30 会每拍喷出一团饱和白块）。
        float peak = amount * 0.90f + burst * 0.045f;
        // 每个发烟点独立的缓慢脉动：烟柱一节一节地吐出，天然形成团块与空隙
        peak *= 0.72f + 0.42f * MathF.Sin((float)t * 0.90f + e.Phase * 2.3f);
        if (peak <= 0f) return;

        // 鼓点沿当地风向喷出一股冲击
        float kick = burst * 26f * (0.7f + 0.3f * gust);
        float ka = 0f;
        if (kick > 0.01f)
        {
            Wind(Math.Clamp(eu / GW, 0f, 1f), Math.Clamp(ev / GH, 0f, 1f), t, gust, 1f,
                out float wxa, out float wya);
            ka = MathF.Atan2(wya, wxa) + (float)(_rng.NextDouble() - 0.5) * 0.6f;
        }
        float kx = MathF.Cos(ka) * kick * 0.55f;
        float ky = MathF.Sin(ka) * kick * 0.55f;

        for (int dy = -ry; dy <= ry; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= GH) continue;
            int row = y * GW;
            for (int dx = -rx; dx <= rx; dx++)
            {
                float q = dx * dx * inv2sx2 + dy * dy * inv2sy2;
                if (q > 2.5f) continue;
                int x = cx + dx;
                if (x < 0 || x >= GW) continue;
                float g = MathF.Exp(-q);
                int i = row + x;
                float d = _den[i] + peak * g;
                _den[i] = d < 0.55f ? d : 0.55f;
                // 极轻的常态扰动让柱缘柔碎（过强会在源头卷出气泡环）；鼓点再加方向冲击
                float jitter = g * (0.40f + burst * 1.2f);
                _vx[i] += kx * g + (float)(_rng.NextDouble() - 0.5) * jitter;
                _vy[i] += ky * g + (float)(_rng.NextDouble() - 0.5) * jitter;
            }
        }
    }

    private void BoxBlur(float[] src, float[] dst, float alpha)
    {
        float center = 1f - alpha * 2f;
        float q = alpha * 0.5f;
        for (int y = 0; y < GH; y++)
        {
            int row = y * GW;
            for (int x = 0; x < GW; x++)
            {
                int i = row + x;
                if (x == 0 || y == 0 || x == GW - 1 || y == GH - 1)
                {
                    dst[i] = src[i];
                    continue;
                }
                dst[i] = src[i] * center
                    + (src[i - 1] + src[i + 1] + src[i - GW] + src[i + GW]) * q;
            }
        }
    }

    // ── 合成到位图 ───────────────────────────────────────────────────────

    private unsafe void Composite(VisualizerFrame frame)
    {
        int w = _bmp!.PixelWidth, h = _bmp.PixelHeight;
        int stride = _bmp.BackBufferStride;
        byte* basePtr = (byte*)_bmp.BackBuffer;

        byte bgR = frame.Background.R, bgG = frame.Background.G, bgB = frame.Background.B;

        // 近白烟雾，仅轻微染上主题强调色（保持参考图的黑白灰风烟影调）
        float tintR = Math.Clamp(214 + frame.Accent.R * 0.10f, 0f, 255f);
        float tintG = Math.Clamp(214 + frame.Accent.G * 0.10f, 0f, 255f);
        float tintB = Math.Clamp(214 + frame.Accent.B * 0.10f, 0f, 255f);

        float scaleX = (GW - 1) / (float)Math.Max(1, w - 1);
        float scaleY = (GH - 1) / (float)Math.Max(1, h - 1);
        uint seed = _grainSeed;

        for (int y = 0; y < h; y++)
        {
            float fv = y * scaleY;
            byte* row = basePtr + y * stride;
            for (int x = 0; x < w; x++)
            {
                float fu = x * scaleX;
                float d = Bilerp(_den, GW, GH, fu, fv);
                if (!float.IsFinite(d)) d = 0f;
                float e = Bilerp(_edge, GW, GH, fu, fv);
                if (!float.IsFinite(e)) e = 0f;

                // 烟幕本体：filmic 曲线——薄烟也能浮现为灰纱，厚处压在中灰以下，绝不白浆
                float dc = Math.Clamp(d, 0f, 1f);
                float body = dc / (dc + 0.28f) * 0.56f;
                // 烟心：极厚处仅一点柔和高光
                float core = dc * dc * 0.05f;
                // 折叠棱线/轮廓：幂函数收成纤细亮褶；软遮罩避免在空底显纹；
                // 厚烟区棱光略衰减，避免糊成白块。
                float mask = Math.Clamp(d * 9f, 0f, 1f);
                float thickFade = 1f - Math.Clamp((d - 0.50f) / 0.25f, 0f, 1f) * 0.5f;
                float fold = MathF.Pow(Math.Clamp(e * 1.75f, 0f, 1f), 1.6f) * mask * 0.95f * thickFade;

                float v = MathF.Min(1.0f, body + core + fold);

                // 胶片颗粒（仅烟幕内）
                float presence = v < 1f ? v : 1f;
                v += (Hash01((uint)x, (uint)y, seed) - 0.5f) * 0.045f * presence;
                if (v < 0f) v = 0f;

                byte* px = row + x * 4;
                px[0] = (byte)Math.Clamp(bgB + tintB * v, 0f, 255f);
                px[1] = (byte)Math.Clamp(bgG + tintG * v, 0f, 255f);
                px[2] = (byte)Math.Clamp(bgR + tintR * v, 0f, 255f);
                px[3] = 255;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Bilerp(float[] f, int gw, int gh, float x, float y)
    {
        if (!float.IsFinite(x)) x = 0f;
        if (!float.IsFinite(y)) y = 0f;
        if (x < 0f) x = 0f;
        else if (x > gw - 1) x = gw - 1;
        if (y < 0f) y = 0f;
        else if (y > gh - 1) y = gh - 1;

        int x0 = (int)x, y0 = (int)y;
        int x1 = x0 + 1;
        if (x1 >= gw) x1 = x0;
        int y1 = y0 + 1;
        if (y1 >= gh) y1 = y0;
        float fx = x - x0, fy = y - y0;
        int r0 = y0 * gw, r1 = y1 * gw;
        float a = f[r0 + x0], b = f[r0 + x1];
        float c = f[r1 + x0], dd = f[r1 + x1];
        return a * (1 - fx) * (1 - fy) + b * fx * (1 - fy)
             + c * (1 - fx) * fy + dd * fx * fy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Hash01(uint x, uint y, uint seed)
    {
        uint h = (x * 73856093u) ^ (y * 19349663u) ^ (seed * 83492791u);
        h = (h ^ (h >> 15)) * 2246822519u;
        h ^= h >> 13;
        return (h & 255) * (1f / 255f);
    }

    public void Cleanup()
    {
        _bmp = null;
        _den = _denTmp = _vx = _vy = _vxTmp = _vyTmp = _edge = null!;
    }
}
