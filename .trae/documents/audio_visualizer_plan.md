# 音频反应式可视化模式 - 完整实施规划

## 一、需求分析

### 1.1 功能概述

为 XSTAR 播放器新增一个**音频反应式可视化模式**（visualizer），用户可在 normal → cd → visualizer 三种显示模式间循环切换。在 visualizer 模式下，用户可在设置中切换 **7 种**不同的可视化风格，全部根据实时音频数据（FFT 频谱 + PCM 波形）动态渲染。

### 1.2 7 种可视化风格总览

| #  | 风格                | 类型 | 音频响应方式               | 实现复杂度  | 主要数据源         |
| -- | ----------------- | -- | -------------------- | ------ | ------------- |
| S1 | **柱状 Bars**       | 经典 | 各频段 → 柱高             | ⭐ 低    | FFT 频谱        |
| S2 | **圆形 Radial**     | 经典 | 各频段 → 半径方向辐射         | ⭐ 低    | FFT 频谱        |
| S3 | **波形 Wave**       | 经典 | PCM 样本 → 曲线          | ⭐⭐ 中   | PCM 波形        |
| S4 | **星尘粒子 Stardust** | 星空 | 低频→呼吸，中频→闪烁，高频→爆裂    | ⭐⭐ 中   | FFT 频谱        |
| S5 | **银河旋臂 Galaxy**   | 星空 | 低频→旋转膨胀，中频→亮度波       | ⭐⭐⭐ 中高 | FFT 频谱        |
| S6 | **脉冲星云 Nebula**   | 星空 | beat detector → 脉冲扩张 | ⭐⭐⭐ 中高 | FFT 频谱 + beat |
| S7 | **极光光束 Aurora**   | 氛围 | 三段频段 → 三条光带振幅        | ⭐⭐ 中   | FFT 频谱        |

### 1.3 核心功能需求

| 编号  | 需求                   | 优先级 | 说明                                                |
| --- | -------------------- | --- | ------------------------------------------------- |
| R1  | FFT + PCM 数据采集       | P0  | BassPlayerEngine 提供实时音频数据                         |
| R2  | 可视化视图容器              | P0  | Stage 中的 VisualizerView + WriteableBitmap         |
| R3  | 7 种渲染器               | P0  | 策略模式，全部实现                                         |
| R4  | 风格切换                 | P0  | 设置中可切换；visualizer 模式下快捷按钮也可切换                     |
| R5  | 三态循环切换               | P0  | ViewModeButton: normal → cd → visualizer → normal |
| R6  | 设置持久化                | P0  | AppState 中保存 visualizer 模式和风格选择                   |
| R7  | 主题色联动                | P0  | 所有渲染器接收 AccentColor，跟随封面取色                        |
| R8  | 性能保护                 | P0  | reduceMotion 降帧；非可视化模式 0 开销                       |
| R9  | 歌曲信息叠加层              | P1  | visualizer 模式下可选显示标题/艺术家                          |
| R10 | MediaPlayerEngine 降级 | P0  | 无 FFT 时显示静态背景 + 提示                                |

### 1.4 非功能需求

| 方面  | 目标                             |
| --- | ------------------------------ |
| 性能  | 30fps 稳定；关闭可视化时 CPU 增量为 0      |
| 流畅性 | EMA 指数移动平均，帧间过渡平滑              |
| 可扩展 | 渲染器接口统一，新增风格只需实现接口             |
| 无障碍 | reduceMotion → 帧率降到 10fps，粒子减半 |

***

## 二、技术方案

### 2.1 架构总览

```
┌─────────────────────────────────────────────────────────┐
│                       MainWindow                          │
│                                                          │
│   ViewModeButton (循环)                                   │
│   normal → cd → visualizer                               │
│                   ▲                                      │
│                   │                                      │
│   ┌───────────────┴─────────────────────────────────┐   │
│   │            VisualizerEngine                      │   │
│   │  ┌───────────────────────────────────────────┐   │   │
│   │  │  _visualizerTimer (30fps, 仅在 visualizer) │   │   │
│   │  └───────────────────────────────────────────┘   │   │
│   │                   ▲                              │   │
│   │                   │ 每秒 30 次                     │   │
│   │  GetSpectrumData() + GetWaveformData()            │   │
│   │                   │                              │   │
│   │                   ▼                              │   │
│   │  ┌───────────────────────────────────────────┐   │   │
│   │  │  选中的 IVisualizerRenderer (7 选 1)        │   │   │
│   │  │  ├─ BarsRenderer                           │   │   │
│   │  │  ├─ RadialRenderer                         │   │   │
│   │  │  ├─ WaveRenderer                           │   │   │
│   │  │  ├─ StardustRenderer (星空)                │   │   │
│   │  │  ├─ GalaxyRenderer (银河)                  │   │   │
│   │  │  ├─ NebulaRenderer (脉冲星云)              │   │   │
│   │  │  └─ AuroraRenderer (极光)                  │   │   │
│   │  └───────────────────────────────────────────┘   │   │
│   │                   │                              │   │
│   │                   ▼                              │   │
│   │  WriteableBitmap → VisualizerView.Image.Source    │   │
│   └─────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
```

### 2.2 接口设计

```csharp
// IPlayerEngine 新增
virtual float[] GetSpectrumData(int bands = 64) => Array.Empty<float>();
virtual float[] GetWaveformData(int samples = 512) => Array.Empty<float>();
```

```csharp
// 渲染器接口
public interface IVisualizerRenderer
{
    string StyleId { get; }          // "bars" / "radial" / "stardust" / ...
    string DisplayName { get; }       // 中文显示名
    void Initialize(WriteableBitmap bitmap);
    void Render(VisualizerFrame frame);
    void Cleanup();
}

public sealed record VisualizerFrame(
    float[] Spectrum,          // 64 频段, 0~1
    float[] Waveform,          // PCM, -1~1
    float BassEnergy,          // 低频能量, 0~1
    float MidEnergy,           // 中频能量, 0~1
    float TrebleEnergy,        // 高频能量, 0~1
    bool BeatDetected,         // 本帧是否检测到 beat
    Color Accent,              // 强调色
    double DeltaTimeMs         // 距上一帧的毫秒
);
```

### 2.3 数据流 - FFT 频谱

```
Bass.ChannelGetData(_stream, buffer, DataFlags.FFT512)
    ↓
float[1024] 复数 → float[256] 线性幅值 (sqrt(re²+im²))
    ↓
20 * log10(value + 1e-10) → dB 值
    ↓
聚合到 64 个对数分布的频带 (低频密集, 高频稀疏)
    ↓
EMA 指数移动平均 → 平滑
    ↓
归一化到 0~1
    ↓
VisualizerFrame.Spectrum
```

### 2.4 数据流 - PCM 波形

```
Bass.ChannelGetData(_stream, buffer, DataFlags.Stereo | DataFlags.Float)
    ↓
合并左右声道: (L + R) / 2
    ↓
降采样到 512 点
    ↓
VisualizerFrame.Waveform (-1 ~ 1)
```

### 2.5 Beat Detector (脉冲检测)

```csharp
// 连续监控低频能量, 当能量 > threshold 且 与上一个 beat 间隔 > minInterval
bool DetectBeat(float currentBassEnergy, float bassEMA)
{
    if (currentBassEnergy > bassEMA * 2.2 && currentBassEnergy > 0.3)
        return true;  // 检测到一个 beat (鼓点/重拍)
    return false;
}
```

### 2.6 定时器策略

```csharp
private readonly DispatcherTimer _visualizerTimer = new();

void ApplyDisplayMode()  // 已有方法
{
    if (_displayMode == "visualizer")
    {
        _visualizerTimer.Interval = _reduceMotion 
            ? TimeSpan.FromMilliseconds(100)   // 10fps
            : TimeSpan.FromMilliseconds(33);   // 30fps
        _visualizerTimer.Start();
    }
    else
    {
        _visualizerTimer.Stop();
    }
}
```

### 2.7 七种渲染器设计

#### S1 BarsRenderer (柱状)

```
┌──────────────────────────────┐
│                              │
│  ▁▃▅▇█▇▅▃▁▂▄▆█▆▄▂▁▃▅▇█▇▅▃ │ ← 64 柱，底部对齐
│                              │
│  颜色: AccentColor 渐变       │
│  顶部可选: 峰值保持线          │
└──────────────────────────────┘
```

#### S2 RadialRenderer (圆形)

```
        ╱▂▄▅▇╲
       ▃░░░░░▇
  ▁▃▅░░░░░░░▇▅▃▁
  ▂▄▆░░░░░░░▆▄▂
       ▃░░░░░▇
        ╲▃▅▇╱
  中心 → 向外辐射 64 条臂
  背景: 深色径向渐变
```

#### S3 WaveRenderer (波形)

```
┌──────────────────────────────┐
│                              │
│  ────────────中心基线──────── │
│  ^^^^^vvvv^^^^vvvvvv^^^^^^   │ ← PCM 波形
│                              │
│  可选: 双声道分上下           │
└──────────────────────────────┘
```

#### S4 StardustRenderer (星尘粒子)

```
  🌌 深色星空背景
  
  粒子池: 300~500 个随机星星
  - 低频 → 所有星星呼吸 (径向缩放 + 亮度脉冲)
  - 中频 → 闪烁频率加快, 部分星星漂浮
  - 高频 → 随机星星爆裂出 sparkle
  - beat → 整体画面闪一下
  
  粒子颜色: AccentColor + HSL 色相偏移
  背景: CoverColorExtractor 主色深色版
```

#### S5 GalaxyRenderer (银河旋臂)

```
  🌀 深色星云背景
  
  2~4 条对数螺旋: r = a * e^(b * θ)
  - 低频能量 → 螺旋旋转速度
  - 低频 beat → 核心区域膨胀 + 亮度闪光
  - 中频 → 亮度波沿螺旋臂向外传播
  - 高频 → 螺旋尾端淡出
  
  粒子池: 400 个沿螺旋分布
  背景: 径向渐变 + 轻微噪点
```

#### S6 NebulaRenderer (脉冲星云)

```
  💫 纯像素级渲染, 无粒子
  
  多层径向渐变叠加
  - beat detector 触发 → 云团向外扩张 + 中心闪光
  - 中频能量 → 云团边缘不断蒸发粒子
  - 高频 → 云团颜色色相微偏移
  
  视觉: 一团呼吸的彩色气体
  技巧: 每帧衰减旧像素 + 叠加新脉冲
```

#### S7 AuroraRenderer (极光光束)

```
  🌌 深色背景 + 少量星星
  
  3 条水平光带 (低频/中频/高频各一条)
  - 每条光带形状 = 多层正弦波叠加
  - 能量 → 光带振幅 + 亮度
  - 颜色: AccentColor 做 HSL 偏移
  
  叠加 50~100 个闪烁星星在背景
  视觉: 横向流动的极光
```

### 2.8 性能策略（关键）

项目使用 `AllowsTransparency="True"` → 强制 **WPF 软件渲染**：

| 策略                     | 实现                                                      |
| ---------------------- | ------------------------------------------------------- |
| **WriteableBitmap 直写** | 每个渲染器直接操作 `WriteableBitmap.BackBuffer`，无 `UIElement` 创建 |
| **固定尺寸渲染**             | 位图固定 800×600，通过 Image 按比例缩放显示                           |
| **帧间残留**               | 粒子/像素衰减使用 `old * decay` 公式，不全量重绘                        |
| **减少 GC**              | 渲染器内部分配好的数组/列表不释放复用                                     |
| **延迟加载**               | 切换到某渲染器时才初始化，首次创建粒子池                                    |
| **暂停即停**               | `IsPlaying = false` 时定时器暂停，画面冻结                         |
| **隐藏即停**               | 窗口最小化 / 不可见时定时器暂停                                       |

### 2.9 文件变更清单

| 文件                                                      | 类型     | 说明                                           |
| ------------------------------------------------------- | ------ | -------------------------------------------- |
| `src/Xstar.Music.Core/IPlayerEngine.cs`                 | 修改     | 新增 `GetSpectrumData` + `GetWaveformData` 虚方法 |
| `src/Xstar.Music.App/BassPlayerEngine.cs`               | 修改     | 实现 FFT + PCM 采集、频带聚合、EMA 平滑、beat 检测          |
| `src/Xstar.Music.App/Visualizer/IVisualizerRenderer.cs` | **新增** | 渲染器接口 + VisualizerFrame 数据类                  |
| `src/Xstar.Music.App/Visualizer/VisualizerEngine.cs`    | **新增** | 定时器调度、帧数据组装、风格切换管理                           |
| `src/Xstar.Music.App/Visualizer/BarsRenderer.cs`        | **新增** | 柱状频谱                                         |
| `src/Xstar.Music.App/Visualizer/RadialRenderer.cs`      | **新增** | 圆形频谱                                         |
| `src/Xstar.Music.App/Visualizer/WaveRenderer.cs`        | **新增** | 波形                                           |
| `src/Xstar.Music.App/Visualizer/StardustRenderer.cs`    | **新增** | 星尘粒子                                         |
| `src/Xstar.Music.App/Visualizer/GalaxyRenderer.cs`      | **新增** | 银河旋臂                                         |
| `src/Xstar.Music.App/Visualizer/NebulaRenderer.cs`      | **新增** | 脉冲星云                                         |
| `src/Xstar.Music.App/Visualizer/AuroraRenderer.cs`      | **新增** | 极光光束                                         |
| `src/Xstar.Music.App/MainWindow.xaml`                   | 修改     | 新增 VisualizerView Grid                       |
| `src/Xstar.Music.App/MainWindow.xaml.cs`                | 修改     | 集成 VisualizerEngine、三态切换、设置持久化               |

***

## 三、实施步骤（按依赖顺序）

### Phase 1: 基础数据层

1. **IPlayerEngine 接口扩展** — 添加 `GetSpectrumData` 和 `GetWaveformData` 两个虚方法
2. **BassPlayerEngine FFT 实现** — FFT512 采样、复数→幅值→dB→分箱→EMA→归一化
3. **BassPlayerEngine PCM 实现** — Stereo 通道采样、合并左右声道

### Phase 2: 渲染器基础设施

1. **Visualizer 目录 + 接口** — `IVisualizerRenderer` + `VisualizerFrame`
2. **VisualizerEngine** — 组装帧数据、定时器调度、风格切换、生命周期管理

### Phase 3: 7 种渲染器实现（顺序无关）

1. **BarsRenderer** — 64 柱，底部对齐，渐变着色
2. **RadialRenderer** — 极坐标 64 条辐射臂
3. **WaveRenderer** — PCM 波形居中绘制
4. **StardustRenderer** — 500 粒子池，低频呼吸 + 中频闪烁 + 高频爆裂
5. **GalaxyRenderer** — 4 条对数螺旋 + 400 粒子，低频驱动旋转
6. **NebulaRenderer** — 纯像素级径向渐变，beat 触发脉冲
7. **AuroraRenderer** — 3 条正弦波光带 + 背景星星

### Phase 4: UI 集成

1. **MainWindow\.xaml VisualizerView** — 包含 Image 承载 WriteableBitmap
2. **ApplyDisplayMode 扩展** — 添加 visualizer 分支，管理定时器启停
3. **ViewMode\_Click 三态循环** — normal → cd → visualizer → normal
4. **VisualizerEngine 挂接** — 从 Stage.SizeChanged 同步位图尺寸

### Phase 5: 设置 & 持久化

1. **AppState 新增字段** — `VisualizerStyle` (默认 "stardust")
2. **设置页「可视化」卡片** — 风格选择段控件 + 信息叠加层开关
3. **visualizer 模式下快捷切换** — 标题栏区域增加风格切换按钮组

### Phase 6: 主题 & 打磨

1. **AccentColor 传递** — 所有渲染器每帧接收最新强调色
2. **CoverColorExtractor 联动** — 星云/极光等的背景色从封面取色
3. **Beat Detector 调优** — 在 NebulaRenderer 中使用

### Phase 7: 性能 & 降级

1. **reduceMotion 友好** — 10fps + 粒子减半
2. **MediaPlayerEngine 降级** — 检测引擎类型，显示静态渐变背景 + 提示
3. **暂停/隐藏即停** — IsPlaying=false 或 IsVisible=false 时定时器暂停

***

## 四、设置界面

### 4.1 外观设置页新增

```
┌───────────────────────────────────────────────────┐
│ 可视化                                             │
│                                                    │
│  可视化风格                                         │
│  [柱状] [圆形] [波形] [星尘] [银河] [星云] [极光]    │
│                                                    │
│  在播放时自动进入可视化模式        [开关]            │
│                                                    │
│  显示歌曲信息叠加层                [开关]            │
│                                                    │
└───────────────────────────────────────────────────┘
```

### 4.2 visualizer 模式快捷切换

进入 visualizer 模式后，标题栏右侧图标栏增加一个风格切换按钮（🔀），点击弹出 7 种风格列表的 Popup，快速切换。

### 4.3 AppState 新增字段

```csharp
public string VisualizerStyle { get; set; } = "stardust";
public bool VisualizerAutoEnter { get; set; }
public bool VisualizerShowInfo { get; set; } = true;
```

***

## 五、渲染器色彩策略

所有渲染器统一遵循：

| 渲染器      | 主色调来源                | 背景色           |
| -------- | -------------------- | ------------- |
| Bars     | AccentColor 渐变       | 纯黑 / 深蓝半透明    |
| Radial   | AccentColor          | 深灰径向渐变        |
| Wave     | AccentColor          | 深色 + 少量噪点     |
| Stardust | AccentColor (色相偏移)   | 封面主色深色版 / 深空蓝 |
| Galaxy   | AccentColor + 紫色偏移   | 深紫径向渐变 + 噪点   |
| Nebula   | AccentColor + 色相动态偏移 | 纯黑 + 轻微渐变     |
| Aurora   | AccentColor (三段色相)   | 深空 + 少量星尘     |

***

## 六、风险与应对

| 风险                                        | 影响     | 应对                                              |
| ----------------------------------------- | ------ | ----------------------------------------------- |
| 软件渲染下粒子效果卡顿                               | 低帧率    | 粒子池上限 500；帧间衰减；reduceMotion 减半                  |
| BASS FFT 仅播放时有效                           | 暂停时无数据 | 暂停时定时器暂停，画面冻结                                   |
| MediaPlayerEngine 无 FFT                   | 无可视化   | 降级为静态渐变背景 + 提示文字                                |
| AllowsTransparency 下 WriteableBitmap 显示异常 | 画面消失   | 在 PerformanceMode 中已处理透明窗口，WriteableBitmap 不受影响 |
| 位图过大导致内存/CPU                              | 性能下降   | 固定 800×600 渲染，缩放到 Stage                         |
| 7 个渲染器编译体积增大                              | 可接受    | 每个独立文件，按需初始化                                    |

***

## 七、验证方案

1. **编译**：`dotnet build` 0 warning / 0 error
2. **三态切换**：normal → cd → visualizer → normal 循环正常
3. **7 种渲染器**：全部可切换、可显示、无异常
4. **音频响应**：播放时动，暂停时静止，停止时清零
5. **性能**：关闭可视化 CPU ≈ 0；开启 30fps 稳定
6. **降级**：MediaPlayerEngine 下不崩溃
7. **reduceMotion**：开启后明显降帧
8. **持久化**：切歌/重启后风格和模式保留
9. **主题联动**：切歌换封面 → 强调色 → 可视化色彩同步变化

***

## 八、推荐默认值

考虑到用户刚要求"都做"并强调"酷炫的星空效果"，推荐默认配置：

| 设置                  | 默认值          | 理由            |
| ------------------- | ------------ | ------------- |
| VisualizerStyle     | **stardust** | 星空类最容易感受到音频互动 |
| VisualizerAutoEnter | true         | 切歌时自动进入，体验最好  |
| VisualizerShowInfo  | true         | 可视化时仍能看到歌曲信息  |
| DisplayMode         | normal       | 保持当前默认        |

