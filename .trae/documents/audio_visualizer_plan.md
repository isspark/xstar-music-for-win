# 音频反应式可视化模式 - 实施规划

## 一、需求分析

### 1.1 功能概述
为 XSTAR 播放器新增一个**音频反应式可视化模式**，在播放音乐时根据音频数据呈现动态视觉效果。用户可以在 normal（普通）、cd（唱片）、visualizer（可视化）三种显示模式之间切换。

### 1.2 核心功能需求

| 编号 | 需求 | 优先级 | 说明 |
|------|------|--------|------|
| R1 | **FFT 频谱数据采集** | P0 | 从 BASS 引擎获取实时频域数据，作为可视化的基础输入 |
| R2 | **可视化视图** | P0 | 在 Stage 中渲染可视化效果的独立视图 |
| R3 | **柱状频谱 (Bars)** | P0 | 经典的 EQ 条形图，垂直柱随频率振幅上下跳动 |
| R4 | **圆形频谱 (Radial)** | P1 | 环绕中心向外辐射的频谱环，类似 AIMP / Foobar2000 的环形效果 |
| R5 | **波形 (Waveform)** | P1 | 展示时域波形，左右声道或合成波形 |
| R6 | **全屏切换** | P0 | 与现有 normal ↔ cd 切换按钮集成，增加 visualizer 模式 |
| R7 | **设置持久化** | P1 | 记住用户选择的可视化类型和显示模式 |
| R8 | **主题色联动** | P1 | 可视化色彩跟随当前强调色 (AccentColor)，支持封面取色联动 |
| R9 | **性能保护** | P0 | 尊重 ReduceMotion 设置；非可视化模式下不运行高帧率定时器 |
| R10 | **后备兼容** | P0 | MediaPlayerEngine 引擎下给出降级提示或简化动画 |

### 1.3 非功能需求

| 方面 | 目标 |
|------|------|
| **性能** | 30fps 下 CPU 占用可接受（软件渲染限制）；关闭可视化时 CPU 增量为 0 |
| **流畅性** | 使用插值算法让帧间过渡平滑，避免频谱跳变 |
| **可扩展性** | 渲染器策略模式，后续可快速添加新风格 |
| **无障碍** | `_reduceMotion = true` 时自动降低帧率或使用静态效果 |

---

## 二、技术方案

### 2.1 架构总览

```
┌─────────────────────────────────────────────────┐
│                   MainWindow                      │
│  ┌────────────────────────────────────────────┐  │
│  │  DisplayMode = "visualizer"                │  │
│  │  ┌──────────────────────────────────────┐  │  │
│  │  │   VisualizerView (新 XAML 元素)       │  │  │
│  │  │   ┌──────────────────────────────┐   │  │  │
│  │  │   │  WriteableBitmap / Canvas      │   │  │  │
│  │  │   │  + IVisualizerRenderer 策略    │   │  │  │
│  │  │   └──────────────────────────────┘   │  │  │
│  │  └──────────────────────────────────────┘  │  │
│  └────────────────────────────────────────────┘  │
│                         ▲                         │
│                         │ FFT 数据                 │
│  ┌──────────────────────┴──────────────────────┐ │
│  │         IPlayerEngine.GetSpectrumData()      │ │
│  │         ↓                                    │ │
│  │   BassPlayerEngine : Bass.ChannelGetData     │ │
│  │   MediaPlayerEngine : 空数组 (降级)           │ │
│  └─────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
```

### 2.2 数据流

```
BASS Channel ──→ Bass.ChannelGetData(FFT512) ──→ float[256] (复数幅值)
                                                    │
                                                    ▼
                                              频率分箱 + 平滑 + 对数缩放
                                                    │
                                                    ▼
                                           float[32] 或 float[64] 处理后数据
                                                    │
                                     ┌──────────────┼──────────────┐
                                     ▼              ▼              ▼
                               BarsRenderer   RadialRenderer  WaveRenderer
                                     │              │              │
                                     └──────────────┼──────────────┘
                                                    ▼
                                          WriteableBitmap 更新
```

### 2.3 FFT 数据处理

ManagedBass 的 FFT 返回复数频率分量，需要转换为线性幅值再做后处理：

```csharp
// BASS FFT512 → 256 个复数 bin
float[] buffer = new float[1024];
int result = Bass.ChannelGetData(_stream, buffer, DataFlags.FFT512);
// buffer[0], buffer[2], buffer[4]... 为实部；buffer[1], buffer[3], buffer[5]... 为虚部
// 幅值 = sqrt(re^2 + im^2)
```

**后处理步骤：**
1. **取幅值**：将复数转换为线性幅值
2. **对数缩放**：人类听觉对数，`20 * log10(value)` 转换为 dB
3. **分箱聚合**：将 256 个 bin 聚合为 32 或 64 个频带（参考 EQ 的 10 段频率分布）
4. **平滑插值**：指数移动平均 (EMA) 让数据过渡自然
5. **归一化**：映射到 0~1 用于渲染

### 2.4 渲染器策略模式

```csharp
// 新文件: Visualizer/IVisualizerRenderer.cs
public interface IVisualizerRenderer
{
    void Initialize(WriteableBitmap bitmap);
    void Render(float[] spectrum, Color accentColor);
    void Cleanup();
}
```

三种初始实现：

| 渲染器 | 描述 | 复杂度 |
|--------|------|--------|
| `BarsRenderer` | 垂直柱状图，底部对齐 | 低 |
| `RadialRenderer` | 极坐标环形辐射 | 中 |
| `WaveRenderer` | 时域波形（需要额外获取 PCM 数据） | 中 |

### 2.5 定时器策略

```csharp
// 可视化专用定时器 - 只在 visualizer 模式启动
private readonly DispatcherTimer _visualizerTimer = new() { 
    Interval = TimeSpan.FromMilliseconds(33)  // ~30fps
};
```

- 进入 visualizer 模式时启动
- 退出时停止
- `_reduceMotion = true` 时降低到 500ms (2fps) 或完全禁用

### 2.6 UI 集成方案

**视图切换**：在现有 `Stage` Grid 中新增 `VisualizerPage`，参考 `PlaylistPage` 和 `LyricsPage` 的模式。但由于可视化需要覆盖整个 Stage，更简洁的方案是在 `ApplyDisplayMode()` 中新增第三种模式 `"visualizer"`。

**模式切换按钮**：复用 `ViewModeButton`（当前切换 normal ↔ cd），改为循环切换 normal → cd → visualizer。或者新增独立按钮。

推荐：**循环模式**，保持与现有 UI 一致，减少按钮密度。

### 2.7 性能考量（关键约束）

项目使用 `AllowsTransparency="True"` 导致 WPF 强制软件（CPU）渲染：
- ❌ 不能依赖 GPU
- ❌ 每帧创建大量 `UIElement` 开销大
- ✅ 使用 `WriteableBitmap` 像素级操作（直接修改 back buffer）
- ✅ 帧率上限 30fps
- ✅ 位图尺寸限制在 Stage 实际大小，不要过大
- ✅ 尊重 `_reduceMotion` 设置

---

## 三、文件变更清单

| 文件 | 变更类型 | 说明 |
|------|----------|------|
| `src/Xstar.Music.Core/IPlayerEngine.cs` | 修改 | 添加 `virtual float[] GetSpectrumData(int bands = 32)` 方法 |
| `src/Xstar.Music.App/BassPlayerEngine.cs` | 修改 | 实现 FFT 数据采集 + 后处理 + 频带聚合 |
| `src/Xstar.Music.App/MediaPlayerEngine.cs` | 可能修改 | 如有该文件，添加空数组实现 |
| `src/Xstar.Music.App/Visualizer/IVisualizerRenderer.cs` | **新增** | 渲染器接口 |
| `src/Xstar.Music.App/Visualizer/BarsRenderer.cs` | **新增** | 柱状频谱渲染 |
| `src/Xstar.Music.App/Visualizer/RadialRenderer.cs` | **新增** | 圆形频谱渲染 |
| `src/Xstar.Music.App/Visualizer/WaveRenderer.cs` | **新增** | 波形渲染 |
| `src/Xstar.Music.App/Visualizer/VisualizerEngine.cs` | **新增** | 管理定时器、调度渲染、协调数据 |
| `src/Xstar.Music.App/MainWindow.xaml` | 修改 | 新增 `VisualizerView` Grid + 渲染层容器 |
| `src/Xstar.Music.App/MainWindow.xaml.cs` | 修改 | 集成 VisualizerEngine、display mode 三态切换、设置持久化 |

---

## 四、实施步骤（按依赖顺序）

### Step 1: IPlayerEngine 接口扩展
- 添加 `virtual float[] GetSpectrumData(int bands = 32)` 返回处理后的归一化频谱
- MediaPlayerEngine 保持默认实现（返回空数组）

### Step 2: BassPlayerEngine FFT 实现
- 实现 `GetSpectrumData` 方法
- 使用 `Bass.ChannelGetData` + `DataFlags.FFT512`
- 包含：复数→幅值、对数缩放、频带聚合、EMA 平滑、归一化
- **注意**：FFT 采样要求流处于播放状态（BASS 限制）

### Step 3: 渲染器基础设施
- 创建 `IVisualizerRenderer` 接口
- 创建 `WriteableBitmap` 辅助方法（尺寸适配、back buffer）
- 实现 `VisualizerEngine`（定时器 + 渲染调度）

### Step 4: 柱状频谱渲染器 (BarsRenderer)
- 垂直柱，底部对齐，32~64 柱
- 颜色从下往上渐变（AccentColor → 亮色）
- 柱之间留间隙
- 可选：顶部"峰值保持"效果

### Step 5: 圆形频谱渲染器 (RadialRenderer)
- 极坐标变换
- 中心空心圆 + 向外辐射的频谱臂
- 背景径向渐变（使用 AccentColor）
- 旋转动画（可选，与 CD 风格呼应）

### Step 6: 波形渲染器 (WaveRenderer)
- 需要额外获取 PCM 数据：`Bass.ChannelGetData(_stream, buffer, DataFlags.Stereo)`
- 左右声道分别绘制，或合成单声道
- 居中波形

### Step 7: UI 集成
- 在 `MainWindow.xaml` 中添加 `VisualizerView` Grid（包含一个 Image 元素承载 WriteableBitmap）
- 在 `ApplyDisplayMode()` 中添加 visualizer 模式分支
- 修改 `ViewMode_Click` 循环：normal → cd → visualizer → normal
- 添加可视化风格切换 UI（visualizer 模式下的小工具栏或在设置中）

### Step 8: 设置持久化
- AppState 新增字段：`VisualizerStyle`、`VisualizerActive`
- `LoadAsync()` / `SaveSettings()` 读写
- 默认值：`VisualizerStyle = "bars"`

### Step 9: 主题色联动
- 每个渲染器接收 `ThemeManager.AccentColor`
- 风格背景从 `CoverColorExtractor` 取色（如果有封面）

### Step 10: 性能优化 & 无障碍
- 测试不同渲染器的 CPU 占用
- `_reduceMotion = true` 时降帧
- 暂停播放时停止定时器
- 窗口不可见时停止定时器

---

## 五、设置界面扩展

在"外观"设置页末尾新增「可视化」卡片：

```
┌──────────────────────────────────────────┐
│ 可视化                                    │
│                                           │
│  可视化风格  [柱状] [圆形] [波形]           │
│                                           │
│  在播放时自动进入可视化模式    [开关]       │
│                                           │
│  显示歌曲信息叠加层           [开关]       │
│                                           │
└──────────────────────────────────────────┘
```

---

## 六、风险与应对

| 风险 | 影响 | 应对 |
|------|------|------|
| **软件渲染性能不足** | 低帧率 / 高 CPU | WriteableBitmap 像素操作；帧率上限 30fps；reduceMotion 降帧 |
| **BASS FFT 数据仅播放时有效** | 暂停时无数据 | 暂停时停止定时器，画面保持最后一帧 |
| **MediaPlayerEngine 无 FFT 支持** | 切换引擎后无可视化 | 检测引擎类型，MediaPlayerEngine 下显示静态背景 + 降级提示 |
| **窗口透明时渲染器丢失** | 可视化消失 | WriteableBitmap 不受 AllowsTransparency 影响；已有 PerformanceMode 机制 |
| **封面取色频率过高** | CPU 增量 | 复用已有的 AccentColor 更新时机，仅在切歌时触发 |

---

## 七、验证方案

1. **编译**：`dotnet build` 无 warning/error
2. **运行测试**：
   - normal / cd / visualizer 三态循环切换正常
   - 播放时可视化启动，暂停时停止
   - 三种渲染器均可显示
   - 窗口最小化/还原后恢复正常
   - 主题切换 + 切歌后强调色正确联动
   - 关闭可视化后 CPU 占用归零
3. **降级验证**：
   - 使用 MediaPlayerEngine 时不崩溃，有优雅降级
   - ReduceMotion 开启时帧率显著降低

---

## 八、后续可扩展方向

- [ ] 粒子效果 / 萤火虫 / 烟雾类可视化
- [ ] 频谱 + 波形混合模式
- [ ] 可视化皮肤 / 预设包
- [ ] 窗口透明模式下的"桌面可视化"（无边框全屏）
- [ ] 输出到屏幕保护程序
