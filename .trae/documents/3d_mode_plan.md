# XSTAR 3D 模式 - 完整实施规划

> 参考对象：`D:\Code\Mineradio`（Electron + Three.js r128）的播放页 3D 形态。
> 本文档为规划定稿，实现前请先读本文件；实现中若调整边界，回写本文件。

---

## 一、需求分析

### 1.1 功能概述

为 XSTAR 播放器新增第四种显示模式 **3D 模式**（`displayMode = "mode3d"`），与现有 `normal → cd → visualizer` 并列。3D 模式是一套**完全沉浸式**的播放页：窗口标题栏、播放控制栏、歌曲信息与歌词全部由网页自绘，C# 只负责窗口本体、音频与数据桥接。

### 1.2 选定范围（四项全含）

| 编号 | 功能 | 说明 |
| --- | --- | --- |
| S1 | **封面 3D 粒子云** | 专辑封面转成随音乐形变的粒子场，多形态预设 |
| S2 | **3D 歌单架** | 卡片弧形排列、滚轮选卡、hover 上浮、点击进详情；内容源可切换 |
| S3 | **3D 歌词舞台** | 歌词以 3D 面片存在并卡拉OK 高亮 |
| S4 | **音频反应** | 频谱 / beat 驱动镜头、粒子、卡片脉冲 |

### 1.3 关键决策（已确认）

| 决策点 | 结论 |
| --- | --- |
| 技术路线 | **B. WebView2 + Three.js** |
| 窗口/控制栏 | **完全沉浸式**：隐藏 WPF 标题栏，网页自绘全部 UI 与拖拽区 |
| 后期处理 | **官方 Three.js + EffectComposer/UnrealBloomPass（真 bloom）** |
| 歌单架内容源 | **全部且可切换**：专辑 / 我的歌单+收藏 / 最近播放（历史） |
| 封面覆盖 | `view` 粒子上限、深度估计算法后续可调 |

### 1.4 非功能需求

| 方面 | 目标 |
| --- | --- |
| 性能 | 默认 60fps 渲染，提供 eco/balanced/high 三档质量（DPR 钳制 + bloom 分辨率） |
| 流畅性 | C# 约 30fps 推送音频特征，JS 端插值到渲染帧率 |
| 生命周期 | 非 3D 模式时 WebView2 不加载/不渲染，CPU 增量接近 0；暂停/不可见时冻结 |
| 无障碍 | reduceMotion → 降帧、减粒子、关闭 bloom |
| 可维护 | 所有 3D 参数存 `settings.json`，网内面板实时可调 |
| 许可 | 不拷贝 Mineradio（GPL-3.0）代码或资产；仅用 MIT 依赖（Three.js 等） |

---

## 二、架构设计

### 2.1 总体结构

```
MainWindow (WPF, layered / AllowsTransparency)
├─ Stage (WPF)                          ← 常规模式使用
│   ├─ AlbumView / VisualizerView / PlaylistPage / ...
│   ├─ PlaybackBar
│   └─ SettingsOverlay
└─ ThreeDView (WPF, 跨整窗, 声明在最上)   ← 仅 mode3d 可见
    └─ WebView2 (占满窗口客户区, 不透明)
         └─ 本地 Web App (Three.js)
              ├─ scene.js    场景 / 相机 / bloom / 渲染循环
              ├─ cover.js    封面粒子云
              ├─ shelf.js    3D 歌单架 + 详情页
              ├─ lyrics.js   3D 歌词舞台
              ├─ audio.js    beat/tempo 引擎 + 音频帧缓存
              ├─ chrome.js   自绘标题栏 / 控制栏 / 信息 / 设置面板
              └─ bridge.js   C# 双向消息封装
```

### 2.2 渲染与窗口要点

- **不透明窗口**：进入 3D 模式时把窗口表面涂为不透明（同 `ApplyDisplayMode` 中 visualizer 分支的 `_vizSurfaceBrush` 做法）。透明分层窗口 + WebView2 存在已知显示风险，Phase 0 优先验证；必要时 3D 模式采用非透明窗口方案。
- **airspace**：WebView2 是子 HWND，无法与 WPF 元素叠加，因此 3D 模式内所有 UI 均在网页侧。
- **进出模式**：进入时 `Visibility = Visible` 并启动推流；退出时 `Visibility = Collapsed`、停止推流、暂停渲染循环，保证常规模式零开销。
- **全屏（可选）**：后续可加"沉浸全屏"开关（`WindowState=Maximized` 或全屏），非首期必需。

### 2.3 内容与数据映射

| 内容源 | XSTAR 数据 |
| --- | --- |
| 专辑 | 音乐库专辑聚合（现有 library albums） |
| 我的歌单 + 收藏 | SQLite 自定义歌单 + Favorites |
| 最近播放 | PlayHistory |

---

## 三、C# ↔ JS 桥接协议

### 3.1 加载

- `CoreWebView2.SetVirtualHostNameToFolderMapping("xstar.local", <输出>/ThreeD/web)` → 加载 `https://xstar.local/index.html`。
- 另开映射 `xstar.cache → %APPDATA%\XstarMusic\`，封面直接以 URL 交给 Three.js `TextureLoader`（`https://xstar.cache/artwork/{hash}.jpg`），避免大体积 base64 传输。
- 首期用 `PostWebMessageAsJson`（C# → JS）与 `chrome.webview.postMessage`（JS → C#）双向通信，消息均为 JSON 信封 `{ type, action, ... }`；音频帧量大时可评估二进制通道。
- Phase 0 已落地：`ThreeDHost` 负责虚拟主机映射、桥接与推流；`AudioFeaturePump` 提供频谱/EMA/beat；网页自绘标题栏与控制栏。

### 3.2 C# → JS（`PostWebMessageAsJson`）

| type | 载荷 | 频率 |
| --- | --- | --- |
| `state` | title/artist/album/artworkUrl/durationMs/positionMs/playing/accent/volume/light | 变化时 + 每 15 帧 |
| `audio` | spectrum[64] / bass / mid / treble / beat / dtMs | ~30fps（播放中） |
| `library` | kind(albums\|playlists\|recent) + cards[]（id/title/subtitle/artworkUrl/count） | 请求时 / 内容变化时 |
| `lyrics` | lines[]（timeMs/text） / activeIndex | 换行时（可合并进 state 周期） |

### 3.3 JS → C#（`chrome.webview.postMessage`）

| action | 说明 |
| --- | --- |
| `ready` / `getState` | 握手 / 主动拉取状态 |
| `play` / `pause` / `next` / `prev` | 播放控制 |
| `seek(positionMs)` / `volume(v)` | 进度 / 音量 |
| `getLibrary(kind)` | 拉取指定内容源卡片 |
| `playTrack(id)` | 从歌单架/详情页播放曲目（Phase 2） |
| `setSetting(key, value)` | 回写 3D 参数（Phase 4） |
| `setDisplayMode(mode)` | 切换显示模式（退出 3D / 返回播放页） |
| `windowDrag` / `windowMinimize` / `windowMaximize` / `windowClose` | 自绘标题栏的窗口操作 |

### 3.4 音频特征来源

抽出 `AudioFeaturePump`：复用 `VisualizerEngine` 现有逻辑（64 频段分频、EMA、beat 检测），从 `IPlayerEngine.GetSpectrumData/GetWaveformData` 取数，供 visualizer 与 3D 模式共用，避免重复实现。

---

## 四、Web 端技术栈与许可

| 依赖 | 版本/来源 | 许可 | 用途 |
| --- | --- | --- | --- |
| Three.js | 官方（`three.module.js`） | MIT | 场景/渲染 |
| EffectComposer + UnrealBloomPass | three/examples/jsm | MIT | 真 bloom |
| 其余着色器 / 交互 / beat 引擎 | 自研 | — | 封面粒子、歌单架、歌词、节奏 |

**禁止**：拷贝 Mineradio 的 JS/GLSL 代码与其资产（含 `skull-decimation-points.bin`）。若要复用其代码，本项目须整体改为 GPL-3.0。封面深度估计首期用亮度启发式，AI 深度（transformers.js）后续可选。

---

## 五、分阶段实施

### Phase 0｜宿主与桥接骨架（先验证）
1. 加 `Microsoft.Web.WebView2` 包；`ThreeDView` + `mode3d` 接入 `ApplyDisplayMode`、模式菜单、设置项、`AppState.DisplayMode` 持久化。
2. 虚拟主机映射；Web 端最小可运行页（Three.js 场景 + 一个 `<canvas>` + 帧率显示）。
3. `ThreeDHost`：WebView2 生命周期、`AddHostObjectToScript`、消息收发、`AudioFeaturePump` 推流。
4. 自绘标题栏拖拽/最小化/关闭/退出 3D 桥接打通。
5. **验证项**：透明分层窗口下 WebView2 是否正常显示与 GPU 加速；进出模式是否干净；WPF 常规模式是否零开销。

### Phase 1｜舞台 + 音频反应 + 封面
1. 场景/相机/渲染循环 + 质量档（eco/balanced/high）。
2. 背景星河粒子（自研 shader）。
3. 封面面片 + 粒子云（网格采样、多形态预设、bloom）。
4. 音频驱动：低频→镜头/粒子呼吸，beat→脉冲；网内歌曲信息层。

### Phase 2｜3D 歌单架 + 详情页
1. 卡片弧形布局、窗口化复用（约 11 张）、距离→缩放/透明度/层级。
2. 滚轮选卡、hover 上浮、点击中心进详情、非中心滚动。
3. 动态镜头 / 静态镜头；自动隐藏 / 常驻。
4. 内容源切换（专辑 / 歌单+收藏 / 历史），数据来自桥接 `getLibrary`。
5. 详情面板：曲目行列表、点击 `playTrack`、懒加载。

### Phase 3｜3D 歌词舞台
1. 歌词行 → CanvasTexture → 面片；卡拉OK 进度/高亮。
2. 与歌单架/详情页的层级（歌词默认在架上，详情打开时让位）。

### Phase 4｜网内 UI、设置、打包
1. 控制栏 / 歌曲信息 / 3D 参数面板（架大小·位置·角度·透明度·镜头·显示模式·质量档·歌词开关）。
2. 参数持久化到 `settings.json`；reduceMotion、暂停冻结、不可见停渲染。
3. `installer.iss`：打包 Web 资产 + WebView2 Evergreen 运行时引导。
4. 单测：DTO 序列化、音频特征转换等纯逻辑。

---

## 六、文件变更清单

### 新增
| 文件 | 说明 |
| --- | --- |
| `src/Xstar.Music.App/ThreeD/ThreeDHost.cs` | WebView2 生命周期、映射、桥接、推流 |
| `src/Xstar.Music.App/ThreeD/BridgeMessages.cs` | `state/audio/library/lyrics/theme` DTO |
| `src/Xstar.Music.App/ThreeD/AudioFeaturePump.cs` | 频谱/EMA/beat，visualizer 与 3D 共用 |
| `src/Xstar.Music.App/ThreeD/web/index.html` | 入口 |
| `src/Xstar.Music.App/ThreeD/web/**` | `app.js` / `scene.js` / `cover.js` / `shelf.js` / `lyrics.js` / `audio.js` / `chrome.js` / `bridge.js` / `style.css` / `vendor/**` |

### 修改
| 文件 | 说明 |
| --- | --- |
| `src/Xstar.Music.App/Xstar.Music.App.csproj` | WebView2 包 + web 资产复制到输出 |
| `src/Xstar.Music.App/MainWindow.xaml` | 新增跨整窗 `ThreeDView` + WebView2 |
| `src/Xstar.Music.App/MainWindow.xaml.cs` | 模式接入、桥接回调、持久化、退出路径 |
| `AppState`（MainWindow.xaml.cs） | 3D 参数字段 |
| `installer.iss` | WebView2 运行时引导 + 资产打包 |
| `tests/Xstar.Music.Tests/**` | 新增纯逻辑单测 |

---

## 七、风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| 透明分层窗口 + WebView2 显示兼容 | 黑屏/闪烁 | Phase 0 先验证；必要时 3D 模式改非透明窗口 |
| WebView2 运行时缺失 | 3D 模式不可用 | 安装包加 Evergreen bootstrapper；启动时检测并降级提示 |
| airspace 限制 | 无法 WPF 叠加 | 3D 模式全部 UI 网内实现 |
| 内存/包体增加 | 约 +100~200MB 进程内存 | 非 3D 模式不加载；运行时可回收 |
| 真 bloom 性能 | 低配掉帧 | 质量档 + DPR 钳制；可自动降级为假 bloom |
| 自研 shader 与 Mineradio 差异 | 效果"神似"非 1:1 | 管理预期；按预设逐步逼近 |
| GPL 传染 | 许可风险 | 不拷贝其代码/资产，仅借鉴思路 |
| 模式切换回归 | 影响现有三模式 | 现有单测 + 手动回归清单 |

---

## 八、验证方案

1. **编译/测试**：`dotnet build .\Xstar.Music.sln` 0 error；`dotnet test .\Xstar.Music.sln` 全绿。
2. **模式切换**：normal / cd / visualizer / 3D 四态切换正常，进出 3D 无残留、无卡死。
3. **桥接**：播放控制、进度、音量、内容源切换、曲目播放、3D 参数回写、窗口拖拽/关闭均可用。
4. **音频反应**：播放时镜头/粒子/卡片随频谱与 beat 变化；暂停冻结；停止清零。
5. **性能**：默认质量档 60fps 稳定；eco 档可明显降载；关闭 3D 后 CPU 增量接近 0。
6. **持久化**：重启后 3D 参数与显示模式保留。
7. **打包**：安装包在无 WebView2 运行时的干净机器上能引导安装并正常使用。

---

## 九、推荐默认值

| 设置 | 默认值 | 理由 |
| --- | --- | --- |
| DisplayMode | normal | 不改现有默认 |
| 3D 内容源 | 专辑 | 数据最全、最直观 |
| 镜头模式 | 动态 | 默认跟随聚焦 |
| 歌单架显示 | 自动隐藏 | 不遮挡封面粒子 |
| 质量档 | balanced | 兼顾画质与性能 |
| 歌词舞台 | 开启 | Mineradio 式体验 |
| Bloom | 开启 | 3D 氛围关键 |

---

## 十、实施进度

- **Phase 0 ✅ 已完成**：WebView2 宿主（`ThreeDHost`）、双向桥接（`BridgeMessages`）、`mode3d` 模式接入与持久化；已验证进出模式、普通模式零开销、WebView2 懒加载。
- **Phase 1 ✅ 已完成**：
  - Three.js（0.160，MIT）+ `EffectComposer`/`UnrealBloomPass` 真 bloom；质量档 `eco/balanced/high`；`balanced` 实测约 120fps。
  - 封面粒子云（`cover.js`）：网格采样 + 亮度深度，5 种形态（浮雕 / 波纹 / 球体 / 漩涡 / 爆裂），随 bass/mid/treble/beat 反应。
  - 背景星河（`starfield.js`）、音频驱动镜头摆动与 bloom 脉冲（`app.js`）。
  - 自绘标题栏/控制栏/进度（`chrome.js`），封面经 `ArtworkBridge` 以 data URI 下发（避免跨源），无封面时用占位图。
  - 网页崩溃/异常经 `clientError` 写入 `%LOCALAPPDATA%\XstarMusic\3d-client.log` 便于排查。
  - 待办（后续阶段）：质量档切换 UI（Phase 4）；封面缺失时占位图已处理。

### 已知问题
- 部分 FLAC 的 `_discArtwork` 为空（内嵌封面未提取），3D 封面走占位图；属既有元数据问题，非 3D 链路缺陷。

- **Phase 2 ✅ 已完成**：
  - 3D 歌单架（`shelf.js`）：卡片弧形堆叠、窗口化复用（11 张池）、滚轮选卡、hover 上浮、点击中心进详情、点击播放按钮直接播放。
  - 二级详情面板：曲目行渲染、滚轮翻页、点击行播放。
  - 内容源切换：专辑 / 歌单（含「收藏」虚拟歌单）/ 最近播放，`getLibrary` 动态取数；卡片封面按需 `getCardArtwork` 回传（160px data URI）。
  - 控制栏：来源分段、自动隐藏/常驻、侧栏/舞台、动态/静态镜头；默认常驻显示。
  - 播放经 `playTrack` 复用现有 `_songs` 队列与 `SelectSong`。
  - 待办：舞台模式布局精修、封面详情页歌词避让（Phase 3）。

- **Phase 3 ✅ 已完成**：3D 歌词舞台
  - `lyrics.js`：歌词行渲染为 3D 空间中的浮动面片，当前行居中放大、白色高亮 + 强调色辉光（配合 bloom），相邻行按距离淡出。
  - C# 侧按需推送：换歌时推全量歌词（`lyrics`），行切换时推 `lyricIndex`（事件驱动，仅变化时发）。
  - 层级：歌词 renderOrder 310 高于歌单架卡片（300）、低于详情面板（330）。
  - 控制栏「词」按钮切换显隐；歌词显示时自动隐藏底部歌曲信息避免重叠。

- **Phase 4 ✅ 已完成**：设置面板 + 持久化 + 降级 + 打包
  - 网页内「外观设置」面板：渲染质量（节能/均衡/高画质）、辉光开关、3D 歌词开关、歌单架大小/左右/上下/侧向角度/透明度滑条。
  - 所有 3D 参数经 `setSetting` 回写 `settings.json`（`AppState.ThreeD*`），进入 3D 时由 `settings` 消息整包下发并应用。
  - 质量档运行时生效（DPR + bloom）；`reduceMotion` 降帧至 ~30fps。
  - 安装包：`installer.iss` 增加 WebView2 Evergreen 运行时检测（缺省时静默安装随包的 `installer\MicrosoftEdgeWebView2Setup.exe`，否则提示下载页）；`dotnet publish` 已包含 `ThreeD/web/**` 与 `WebView2Loader.dll`。
  - 版本号提升至 1.2.0（installer.iss、App/Core csproj）。

- **全部阶段完成**。后续可选：详情页"专辑内连续播放"、真实封面在歌单架上的加载优化、更多封面形态预设、3D 歌词逐字卡拉OK。

### 已验证
- `dotnet build`：0 error；`dotnet test`：26/26。
- 安装包编译成功：`dist\XstarMusic-Setup-1.2.0.exe`。
- 3D 设置持久化与恢复经截图验证（球体封面、缩小/倾斜/上移歌单架、关闭辉光与歌词、高画质档均生效）。

### 后续修复（v1.2.0）
- **窗口拖动**：WebView2 独占鼠标导致 WPF `DragMove()` 失效，改为 Win32 `ReleaseCapture()` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`。
- **控制按钮变形**：高 DPI 下底栏 flex 收缩，圆形按钮被压成椭圆、标签折行。修复为 `flex:0 0 auto` + `inline-flex` 居中 + `white-space:nowrap` + 进度条 `clamp` 自适应。
- **均衡/高画质发白发灰**：`EffectComposer` 缺少色彩空间转换，加入 `OutputPass`。
- **歌词爆白**：歌词改为独立场景 + 独立 `RenderPass`（在 bloom 之后渲染），不再被泛光叠加；当前行始终垂直居中。
- **背景可调**：设置面板新增背景颜色（取色器）与背景图片（C# 文件对话框 → data URI，带遮罩/暗角避免泛光与喧宾夺主）；`ThreeDBackgroundColor` / `ThreeDBackgroundImage` 持久化。
- **粒子模糊**：点精灵边缘由柔和渐隐改为近似实心圆（`smoothstep(0.34,0.5)`），点尺寸由 1.6× 格距降到 1.15× 并收窄随机变化，减少重叠；泛光 `radius` 0.45 → 0.32；封面纹理 288 → 384。
- **律动偏弱**：新增 shaping 后的 `audio.pulse` / `audio.energy`（增益 + 曲线），粒子 shader 振幅随低频大幅增强（浮雕深度、球体呼吸、漩涡转速、律动位移），群组缩放与相机推近、泛光脉冲同步加强。
- **形态细化**：网格密度提高（eco 112 / balanced 160 / high 200）；点尺寸基数 1.15 → 1.02，并为球体/波纹/漩涡加 `uSizeScale` 使点更小更密；波纹加入第二谐波。原「爆裂」径向炸开改为「**律动**」——整面粒子沿 Z 轴随低频/节拍前后起伏，形成行波式律动。
- **窗口缩放 / 最大化**：无边框窗口（WindowStyle=None + AllowsTransparency）本身没有非客户区，WebView2 又独占鼠标，故两者都失效。新增 `windowResize(edge)` 桥接：网页四边/四角 5–8px 热区 → C# `ReleaseCapture()` + `WM_NCLBUTTONDOWN(HTLEFT…HTBOTTOMRIGHT)` 交由系统缩放；WndProc 处理 `WM_GETMINMAXINFO`，把最大化尺寸/位置夹到显示器工作区，避免无边框窗口最大化时盖住任务栏（「假全屏」）。
- **粒子外观设置**：设置面板新增「粒子大小（0.4–2.5×）/ 粒子光晕（0–2.5× bloom）/ 粒子数量（0.5–1.6× 网格重建）/ 粒子颜色（单色·自然·彩色 + 单色取色器）」。颜色由 shader `uColorMode` 实现：单色用 `uMonoColor`（默认跟随主题强调色，可用取色器自定）× 亮度，自然用封面原色，彩色按 uv+亮度做色相扫描；光晕在帧循环里以 `baseBloom * glow` 缩放，节拍脉冲叠加其上。全部经 `ThreeDParticleSize/Glow/ParticleCount/ParticleColorMode/ParticleColor` 持久化。
- **歌单曝白修复**：歌单架（卡片/详情）原先与封面同在主场景，会被 bloom 叠加导致前几张卡歌名曝白；改为独立 `shelfScene` + 独立 RenderPass（bloom 之后渲染，不受辉光影响），歌词仍在最上层。
- **律动收敛**：`律动` 起伏振幅减半（`uBass*2.6+uBeat*1.7` → `uBass*0.85+uBeat*0.45`），行波频率与随机因子降低，观感更自然。
- **设置面板合并**：原来左上角的「外观设置」入口与底部传输栏的「形态 · xxx」按钮合并为一个面板——入口改为传输栏原形态位置上的齿轮图标（`#btn-appearance`），面板内首行是「形态」5 选一（浮雕/波纹/球体/漩涡/律动），其余外观设置照旧；面板改锚定到右下（`right:16px; bottom:78px`）并加 `max-height + overflow-y` 以适配小窗口。
- **左上角控件并入面板**：删除 `#shelf-controls` 浮层，把「内容来源（专辑/歌单/最近）」「歌单显示」「歌单布局」「镜头」并入统一设置面板顶部区域；面板 `max-height: calc(100vh - 122px)` 保证不遮挡标题栏窗口按钮。
- **设置分区块折叠 + 滚动条样式**：面板改用原生 `<details>/<summary>` 分三块——「形态与粒子」「歌单架」「画面与背景」，点击标题展开/收起（首块默认展开，右侧 chevron 旋转指示）；`#settings-panel::-webkit-scrollbar` 定制为细半透明圆角滑块，与玻璃拟态面板协调。
- **歌词按钮图标化**：传输栏「词」文字按钮改为内联 SVG 文本行图标（`stroke: currentColor`，激活态随 `.active` 变为强调色背景+深色描边），保留 `title`/`aria-label="3D 歌词"`。
- **歌单架选项平铺**：设置里的「歌单显示 / 歌单布局 / 镜头」由点击循环的单按钮改为分段选择（`#set-presence` / `#set-shelf-mode` / `#set-camera`，DOM `data-v` 即取值），与「形态/内容来源/渲染质量/粒子颜色」风格一致，不再需要反复点击切换。










