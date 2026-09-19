# XSTAR 桌面壁纸模式 - 实施规划

> 前置：3D 模式已完成（见 `.trae/documents/3d_mode_plan.md`），WebView2 + Three.js 场景、桥接、音频推流均已可用。
> 本文档为规划定稿，实现前先读本文件；实现中若调整边界，回写本文件。

---

## 一、需求与结论

### 1.1 目标

把现有 3D 场景**内嵌为 Windows 桌面壁纸**：渲染在桌面图标层之后，随音乐律动，且独立于播放器主窗口运行（主窗口可缩到托盘，壁纸继续）。

### 1.2 技术结论

Windows 上实时 3D 壁纸唯一可行方案是 **WorkerW 重父化**：

1. 向 `Progman` 发送 `0x052C` 让 Explorer 在图标层后创建 `WorkerW`；
2. `EnumWindows` 找到「其后紧跟 `WorkerW` 兄弟」的 `SHELLDLL_DefView` 宿主，取其后的 `WorkerW`；
3. `SetParent(壁纸窗口, WorkerW)`，窗口铺满主显示器。

`IDesktopWallpaper` / `SystemParametersInfo(SPI_SETDESKWALLPAPER)` 只能设静态图，不适用于实时 3D。

### 1.3 关键决策（已确认）

| 决策点 | 结论 |
| --- | --- |
| 与现有模式关系 | **纯加法**：`normal / cd / visualizer / mode3d` 与窗口内 3D 全部不动 |
| 壁纸宿主 | **独立不透明窗口** + 第二个 WebView2；不复用透明分层的 `MainWindow` |
| 鼠标交互 | **可开关**：交互时切到顶层全屏窗口（覆盖图标）以接收鼠标；关闭回到图标层之下的纯背景壁纸（见 §七） |
| 显示器 | **仅主显示器**，单实例 |
| 壁纸内容 | 封面粒子 + 星河 + 3D 歌词（`chrome`/`shelf` 隐藏，歌词可开关） |
| 开机自启 | **本期不做**，W4 再评估 |

---

## 二、架构设计

### 2.1 总体结构

```
MainWindow (WPF, layered — 不变)
   └─ ThreeDView / ThreeDWebView        ← 窗口内 3D 模式，保持现状

WallpaperController (新, MainWindow 持有)
   ├─ DesktopWallpaper (新, Win32 interop)
   │     FindWorkerW / Attach / Detach / SetExStyle / PrimaryMonitorBounds
   └─ WallpaperWindow (新, 不透明无边框 Window)
         └─ WallpaperWebView (第二个 WebView2)
              └─ 复用 ThreeDHost + AudioFeaturePump + BridgeMessages
                   └─ https://xstar.local/index.html?wallpaper=1
```

### 2.2 为什么独立窗口

- 主窗口 `AllowsTransparency=True`（`MainWindow.xaml:10`）。**分层窗口 + WebView2 + `SetParent` 已知会黑屏/闪烁**，壁纸窗口必须不透明、非分层。
- 主窗口承载全部应用 UI，仍需正常使用；壁纸必须能独立于主窗口存活（缩托盘时继续）。
- 独立窗口在未启用壁纸时零创建、零开销。

### 2.3 与主窗口的数据耦合

壁纸需要 `state / artwork / lyrics / 播放控制`，这些当前都在 `MainWindow`（`IThreeDActions` 实现）。

- **本期做法（改动最小）**：`WallpaperController` 复用 `MainWindow` 作为 `IThreeDActions`，直接把 `ThreeDHost` 指向它，从而复用同一份歌单/收藏/历史/歌词逻辑。主窗口行为不变（壁纸模式下窗口操作类回调为 no-op）。
- **后续可选重构**：把 `ThreeDGet*Json` 抽成共享 `ThreeDBridgeService`，主窗口与壁纸都依赖它。W3 再评估。

---

## 三、Win32 要点

### 3.1 WorkerW 查找 / 挂载

```
progman = FindWindow("Progman", null)
SendMessageTimeout(progman, 0x052C, 0, 0, SMTO_NORMAL, 1000, out _)
EnumWindows: 若某 top 窗口含子窗口 "SHELLDLL_DefView"
    → workerw = FindWindowEx(null, top, "WorkerW", null)
SetParent(wallpaperHwnd, workerw)
```

- 不设 `WS_CHILD`（会破坏 WPF 窗口）；保持 `WS_POPUP` 仅 `SetParent`，为业界通行做法。
- `FindWindowEx` 链式查找在 Windows 10/11 的桌面结构下均可用。

### 3.2 鼠标穿透与焦点

- 壁纸窗口扩展样式：`WS_EX_NOACTIVATE`（不抢焦点）| `WS_EX_TOOLWINDOW`（不进 Alt-Tab）；默认再加 `WS_EX_TRANSPARENT`（鼠标穿透，保证桌面右键/框选正常）。
- 可交互开关：移除 / 重加 `WS_EX_TRANSPARENT`。必要时对 WebView2 子 HWND 同样设置。

### 3.3 桌面生命周期

- **Explorer 重启**：监听 `RegisterWindowMessage("TaskbarCreated")`，重新查找 WorkerW 并重挂。用 message-only `HwndSource` 承载，避免耦合主窗口 WndProc。
- **分辨率 / DPI 变化**：`WM_DISPLAYCHANGE` / `SystemEvents.DisplaySettingsChanged` → 重设窗口矩形，必要时重挂。
- **锁屏 / 会话切换**：暂停渲染（W4）。

---

## 四、Web 端改动

1. `app.js` 识别壁纸模式（`location.search` 的 `wallpaper=1` 或设置消息 `wallpaper` 标志）。
2. 壁纸模式：隐藏全部 chrome（标题栏 / 传输栏 / 设置）；`shelf` 默认隐藏；禁用 orbit / pointer 捕获；`pointer-events:none`。
3. 默认质量档 `eco`，尊重 `reduceMotion`；暂停 / 不可见时冻结渲染循环。
4. 保留：封面粒子云、星河、音频反应、3D 歌词（可开关）。
5. 非壁纸模式行为与现状**完全一致**。

---

## 五、设置与生命周期

新增持久化字段（`AppState`）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `WallpaperEnabled` | false | 启用桌面壁纸 |
| `WallpaperInteractive` | false | 交互模式：浮到桌面之上接收鼠标（覆盖图标） |

> 注：壁纸的**外观完全跟随 3D 模式**（形态 / 粒子 / 背景 / 歌词 / 质量），不再单设 `WallpaperQuality` / `WallpaperShowLyrics`；窗口内 3D 设置变化时实时 `PushSettings` 同步到壁纸。
| `WallpaperQuality` | eco | 壁纸渲染质量档 |

- 入口：主设置面板 + 托盘菜单「桌面壁纸」开关。
- 启用：创建 `WallpaperWindow` → 初始化 WebView2 / `ThreeDHost` → 挂到 WorkerW。
- 停用：`SetParent` 还原 → 关 WebView2 / 释放 host → 关闭窗口。
- 应用启动时若 `WallpaperEnabled` 自动挂载；退出时确保还原。

---

## 六、分阶段实施

### W0｜可行性验证（先做）
1. `DesktopWallpaper` interop：WorkerW 查找 / `SetParent` / ex-style / 主屏矩形。
2. 最小 `WallpaperWindow`（不透明无边框 + WebView2）+ `WallpaperController`，复用 `ThreeDHost`。
3. 托盘菜单临时开关，手工验证。
4. **验证项**：图标层后可渲染、无黑屏、鼠标穿透生效、Explorer 重启自动重挂、停用后桌面恢复。

### W1｜宿主完善
- 主显示器定位、`WM_DISPLAYCHANGE` / DPI 处理、Explorer 重挂稳固化、启动自动挂载与退出还原。
- 失败降级：挂载失败时回退普通无边框全屏并提示。

### W2｜网页壁纸模式
- `wallpaper` 标志、隐藏 chrome/shelf、鼠标穿透、质量降级、可选歌词、指针逻辑禁用。

### W3｜集成与设置
- `AppState` 字段、设置面板 UI、托盘菜单、持久化、稳定运行；评估 `ThreeDBridgeService` 重构。

### W4｜加固与打包
- 锁屏 / 全屏应用暂停、多显示器评估、性能档与电池策略、可选开机自启（`--wallpaper` + 启动项）、纯逻辑单测、`installer.iss` 核对。

---

## 七、风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| WebView2 挂 WorkerW 合成兼容 | 黑屏/闪烁 | W0 先验证；失败降级为普通全屏窗并提示 |
| 分层窗口误用 | 无法渲染 | 壁纸窗口 `AllowsTransparency=False`，硬约束 |
| Explorer 重启销毁 WorkerW | 壁纸消失 | 监听 `TaskbarCreated` 重挂 |
| 第二个 WebView2 内存 | +100~200MB | 仅启用壁纸时创建；停用即释放 |
| 鼠标穿透 / 桌面右键冲突 | 桌面不可用 | 默认穿透；交互模式为显式开关 |
| **Win11 可见性 vs 鼠标互斥** | 交互时壁纸消失 | **已确认无法在图标层之下兼得**：桌面壁纸层由 DWM 合成在普通子窗口之上，只有 `WS_EX_TRANSPARENT`（后绘制）的子窗口可见，而该位正是鼠标穿透的开关。临时浮到 Progman 顶层+清透明位同样被盖住。故**取消鼠标交互**，壁纸纯背景（截屏验证） |
| 显示器热插拔 / DPI | 尺寸错位 | `WM_DISPLAYCHANGE` 重设矩形 |
| 模式切换回归 | 影响现有四态 | 纯加法；现有单测 + 手动回归清单 |

---

## 八、验证方案

1. 编译：`dotnet build .\Xstar.Music.sln` 0 error；测试：`dotnet test .\Xstar.Music.sln` 全绿。
2. 现有四模式（normal / cd / visualizer / mode3d）行为与视觉无变化。
3. 壁纸启用：场景在桌面图标后渲染、随音乐律动；桌面右键/框选正常（穿透）。
4. Explorer 重启（任务管理器结束 explorer.exe）后壁纸自动恢复。
5. 停用壁纸：桌面恢复原壁纸，进程内存回落。
6. 主窗口缩托盘后壁纸继续运行。

---

## 九、文件变更清单

### 新增
| 文件 | 说明 |
| --- | --- |
| `src/Xstar.Music.App/Wallpaper/DesktopWallpaper.cs` | Win32 interop：WorkerW 查找/挂载/卸载、ex-style、主屏矩形、TaskbarCreated |
| `src/Xstar.Music.App/Wallpaper/WallpaperWindow.xaml(.cs)` | 不透明无边框宿主 + WebView2 |
| `src/Xstar.Music.App/Wallpaper/WallpaperController.cs` | 启停编排、生命周期、与 `ThreeDHost`/`IThreeDActions` 对接 |

### 修改
| 文件 | 说明 |
| --- | --- |
| `src/Xstar.Music.App/MainWindow.xaml.cs` | 托盘菜单开关、`AppState` 字段、设置持久化、退出还原 |
| `src/Xstar.Music.App/ThreeD/web/app.js` | 壁纸模式标志与分支 |
| `src/Xstar.Music.App/ThreeD/web/chrome.js` | 壁纸模式隐藏 |
| `src/Xstar.Music.App/ThreeD/web/style.css` | 壁纸模式样式 |
| `installer.iss` | 若做开机自启再加启动项 |

---

## 十、实施进度

- **W0 ✅ 已完成**（2026-09-19）：
  - `DesktopWallpaper`：WorkerW 查找 / `SetParent` / ex-style（`WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TRANSPARENT`）/ 主屏矩形 / 后代窗口点击穿透 / `TaskbarCreated` 重挂。
  - `WallpaperWindow`：不透明无边框窗口 + 独立 WebView2。
  - `WallpaperController`：启停编排，复用 `ThreeDHost`（新增可选 `userDataFolder`，壁纸用独立 `WebView2Wallpaper` 目录避免与环境冲突）；日志写入 `%LOCALAPPDATA%\XstarMusic\wallpaper.log`。
  - 托盘菜单「桌面壁纸（开关）」；DEBUG 环境变量 `XSTAR_WALLPAPER_TEST=1` 启动即挂载，便于验证。
  - **关键发现**：本机 Windows 11（build 26200）桌面结构与经典布局不同——壁纸 `WorkerW` 是 **`Progman` 的子窗口**（位于 `SHELLDLL_DefView` 之后），而非顶层兄弟节点。`FindWorkerW` 已同时兼容两种布局。
  - **另注**：`GetParent` 对非 `WS_CHILD` 窗口返回 owner（0），检测是否已挂载需用 `GetAncestor(GA_PARENT)`，已采用。
  - **已验证**：`attached=True`；在窗口内 3D 关闭（`DisplayMode=normal`）时，壁纸宿主 WebView2 仍渲染出完整场景（截图 1.9MB 封面粒子 + 星河）。
  - **待人工视觉确认**：场景在桌面图标层之后显示、桌面右键/框选正常（穿透）、Explorer 重启后自动恢复。
- **W1 ✅ 基本完成**（2026-09-19）：
  - 主显示器全屏定位（`FitPrimary`，物理像素）。
  - `WM_DISPLAYCHANGE` 处理：分辨率/显示器布局变化时重设矩形并重挂。
  - Explorer 重启重挂（`TaskbarCreated`，W0 已做）。
  - 退出还原（`OnClosing` → `Stop` → `Detach` + 关窗）。
  - `ThreeDHost` 新增可选 `homeUrl`，壁纸宿主加载 `index.html?wallpaper=1`。
  - 待办：**启动自动挂载**依赖持久化，并入 W3；失败降级当前为返回 false + 提示。
- **W2 ✅ 已完成**（2026-09-19）：
  - `app.js` 识别 `?wallpaper=1`，`body.wallpaper` 隐藏全部自绘 UI（标题栏 / 传输栏 / 歌曲信息 / 设置面板 / HUD / 缩放热区）。
  - 壁纸模式用 no-op shelf 取代 3D 歌单架（仅封面粒子 + 星河 + 3D 歌词）。
  - 鼠标轨道交互保留（由 C# 侧窗口穿透开关控制是否可交互）。
  - **已验证**：截图显示壁纸宿主仅渲染封面粒子 + 星河，无任何 UI。
- **W3 ✅ 已完成**（2026-09-19）：
  - `AppState` 新增 `WallpaperEnabled / WallpaperInteractive / WallpaperShowLyrics / WallpaperQuality`（默认 `false / false / true / eco`），并在 `LoadAsync` / `SaveSettings` 读写。
  - 壁纸拥有**独立**的歌词与质量设置：`ThreeDHost` 新增可选 `settingsProvider`，`WallpaperController` 注入 `BuildWallpaperSettingsJson()`（`BuildThreeDSettingsJson(quality, lyrics)` 参数化，窗口内 3D 沿用原值）。
  - 外观设置新增「桌面壁纸」卡片：启用开关、鼠标交互、显示 3D 歌词、渲染质量三档；改动即时 `PushSettings()` 并持久化。
  - 托盘「桌面壁纸（开关）」改为走 `SetWallpaperEnabledAsync`，与设置项一致并持久化。
  - **启动自动挂载**：`LoadAsync` 读取 `WallpaperEnabled` 后自动 `StartAsync`（DEBUG 下 `XSTAR_WALLPAPER_TEST=1` 强制开启）。
  - **已验证**：无环境变量、仅凭 `settings.json` 的 `WallpaperEnabled=true` 即可自动挂载（`wallpaper.log` 记录 `attached=True`），截图显示封面粒子 + 3D 歌词、无任何 UI、eco 档。
### 修复：鼠标交互（2026-09-19）
- 现象：开启「鼠标交互」后壁纸消失，关闭恢复；实测窗口始终 `attached=True`，非分离/崩溃。
- 定位（截屏对比，`mode=top/desc` 隔离实验）：在 Win11（build 26200）上，**WebView2 子窗口的 `WS_EX_TRANSPARENT` 一旦清除，子窗口便被桌面壁纸层（DWM 合成）盖住** → 变黑/消失。
- 关键结论：
  1. 图标层之下「可见」与「接收鼠标」互斥（把窗口临时浮到 Progman 顶层+清透明位仍被盖住）。
  2. 但**顶层窗口**里，只要顶层自身非透明（可命中），子窗口保留 `WS_EX_TRANSPARENT`（可见）时，WebView2 **仍能收到鼠标**（合成探针日志 `probe pointerdown` 验证：模拟拖拽坐标命中）。
- **最终方案**：交互模式 = **独立的顶层全屏窗口**（不重父化、不 layered），顶层清 `WS_EX_TRANSPARENT|WS_EX_NOACTIVATE`，子窗口保留 `WS_EX_TRANSPARENT`，`HWND_TOPMOST` 抬升后立即 `HWND_NOTOPMOST`。交互时覆盖桌面图标，`Esc` 或开关返回壁纸。
  - 曾尝试对活动 WPF 窗口 `SetParent(null)` 重父化：API 报告 `visible=True/cloaked=0/topmost` 但屏幕不显示，WPF 重父化不可靠 → 改为**每次切换重建窗口**（`SetInteractiveAsync` → `Stop()` + `StartAsync(mode)`）。
  - 曾尝试 layered（`AllowsTransparency=True`）交互窗：WebView2 反而不合成（透出桌面），故用**非 layered 不透明窗**。
- 同步恢复 `WallpaperInteractive` 设置项（外观 → 桌面壁纸），并在 `WallpaperWindow` 构造时按主显示器 DIP 尺寸铺满（否则 WPF 会用 XAML 的 1280×720 覆盖 Win32 全屏）。

### 3D 设置持久化 + 恢复默认（2026-09-19）
- 3D 外观设置均由 `ThreeDOnSetting` → `SaveSettings()` 持久化到 `settings.json`（形态 / 粒子 / 背景 / 歌词 / 质量 / 歌单架布局 / 相机 / 内容源），重启后经 `ThreeDSettingsMessage` 整包恢复。
- 新增「恢复默认配置」：
  - 桥接动作 `resetSettings` → `IThreeDActions.ThreeDResetSettings()` → `ResetThreeDSettings()`：把所有 `_threeD*` 字段重置为默认、`SaveSettings()`、并 `PushSettings()` 同步窗口内 3D 与壁纸两个宿主。
  - 入口：主设置「外观 → 3D 模式」卡片按钮；3D 窗口内设置面板底部按钮（`#btn-reset3d`）。

### 修复：封面粒子曝白（2026-09-19）
- 根因：粒子 HDR 亮度（封面白区 × 亮度系数 × bass 增益）超过 1，而 `renderer.toneMapping` 为默认 `NoToneMapping`，`OutputPass` 直接裁到纯白，再叠加 bloom 形成大面积曝白。
- 修复（`web/scene.js`、`web/app.js`，3D 模式与壁纸共用）：
  - `renderer.toneMapping = ACESFilmicToneMapping`，`toneMappingExposure = 1.3`（高光平滑滚降，不再硬裁白）。
  - `UnrealBloomPass` 阈值 `0.6 → 1.0`（仅真正 HDR 高光泛光），`bloomStrength` balanced `0.8→0.7` / high `1.0→0.9`。
  - beat 驱动的 bloom 脉冲 `beat*1.2+pulse*0.5 → beat*0.6+pulse*0.3`，避免强拍白闪。
- 验证：截图显示白区封面解析为粒子细节，无大面积白斑。

### 修复：外观跟随 3D 模式 + 歌词不滚动（2026-09-19）
- **外观跟随**：壁纸不再使用独立的质量/歌词设置，改为直接复用 `ThreeDGetSettingsJson()`（形态 / 粒子 / 背景 / 歌词 / 质量与 3D 模式一致）；移除 `WallpaperQuality` / `WallpaperShowLyrics` 与 `BuildWallpaperSettingsJson`。窗口内 3D 设置变更时 `ThreeDOnSetting` 末尾 `_wallpaperController.PushSettings()` 实时同步。
- **歌词不滚动**：`PushLyrics` / `PushLyricIndex` / `PushState` / `PushArtwork` 此前只发给窗口内 3D 宿主 `_threeDHost`，壁纸宿主收不到。现 `WallpaperController` 暴露同名方法，`MainWindow` 在原有 8 处推送点同时推给壁纸，`UpdateProgress → UpdateLyricHighlight` 的逐行推进即可驱动壁纸歌词滚动。

- **W4 待开始**：锁屏 / 全屏应用暂停、多显示器评估、可选开机自启（`--wallpaper` + 启动项）、纯逻辑单测、`installer.iss` 核对。
