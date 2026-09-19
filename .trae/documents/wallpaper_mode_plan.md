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

### 新增：空白桌面拖拽旋转（不覆盖图标，2026-09-19）
- 需求：壁纸模式（非交互）下也能拖拽旋转 / 滚轮缩放，但**不得遮挡或拦截桌面图标**，也不用浮到前台全屏窗。
- 方案：全局低级鼠标钩子 + 桌面图标命中测试。
  - `DesktopWallpaper` 新增命中测试：`IsOnPrimary`（主屏矩形）、`IsDesktopPoint`（`WindowFromPoint` 命中 `SysListView32`，即桌面图标层）、`IsOverDesktopIcon`（跨进程 `LVM_HITTEST`，在 explorer 进程分配远端缓冲区 + `SendMessageTimeout`，`LVHT_ONITEM` 判定是否落在图标上）、`DescribePoint`（诊断）。
  - 新增 `WallpaperMouseHook`（`WH_MOUSE_LL`）：仅在 `IsOnPrimary && IsDesktopPoint && !IsOverDesktopIcon` 时介入；左键在空白桌面按下即吞掉（避免框选），拖动超过 3px 阈值后转成 orbit 增量并吞掉后续 move/up；滚轮同理转成 zoom。命中图标、其他窗口、右键全部原样放行（`CallNextHookEx`）。
  - 接线：`WallpaperController` 构造时创建钩子；仅在 `IsRunning && !Interactive` 时 `Install` + `Enabled=true`，`Stop()` 时 `Uninstall`（交互模式下不安装）。orbit 经 `ThreeDHost.PushOrbit(dx,dy,wheel)` → 桥接消息 `{type:"orbit",dx,dy,wheel}` → `web/app.js` 复用既有 `orbit.targetTheta/targetPhi/radiusTarget`（与窗口内拖拽同一套参数与系数）。
- 诊断：环境变量 `XSTAR_WALLPAPER_HOOK_DEBUG=1` 时，每次左键按下记录 `DescribePoint` 判定结果到 `wallpaper.log`。
- **已验证**（模拟输入 + 日志）：空白桌面按下被吞并命中判定 `desktop=True icon=False`；拖拽增量送达 web（`theta 0.00 → 0.46 → 0.59`），滚轮增量送达（`wheel=120`）；交互模式启动日志无 `mouse hook installed`（钩子未安装，符合预期）。

### 新增：无封面时用歌名生成粒子（2026-09-19）
- 需求：3D 模式的封面粒子云在**没有封面图**（解析失败 / 无内嵌图）时，改用**歌名文字**作为粒子形状。
- 实现（`web/cover.js`，3D 模式与壁纸共用）：
  - `makePlaceholder(accent, title)`：无封面时在 512² 画布上把歌名按 CJK 逐字 / 拉丁逐词换行、二分收缩字号至适配 82% 宽 × 78% 高，居中绘制白色文字（accent 描边辉光）+ 暗底 accent 径向微光；空标题回退原装饰横线。
  - `cover.setTitle(title)`：记录歌名；仅在**非图片模式**下重建文字纹理。
  - `setImage` 成功进入图片模式；解码失败或收到无 data 的 `artwork`（→ `clearImage()`）则回退文字纹理；`setAccent` 在文字模式下同步重建。
  - `web/app.js` 的 `state` 分支调用 `cover.setTitle(message.title)`。
- **已验证**：临时注入 `{type:"state",title:"夜空中最亮的星"}` + 无 data 的 `artwork` 后 `CapturePreviewAsync` 截图，粒子云清晰构成歌名文字（临时注入已移除）。

### 新增：歌词渲染模式（默认 / 粒子，2026-09-19）
- 需求：新增设置「歌词渲染」= 默认 / 粒子。选「粒子」时当前歌词行渲染进粒子云并隐藏原 3D 歌词文字；无歌词时回退封面/歌名粒子（与上一节一致）。
- 实现：
  - 设置键 `lyricMode`（`"default"` / `"particle"`）：`AppState.ThreeDLyricMode` ← `ThreeDSettingsMessage.LyricMode`；`MainWindow` 字段、`LoadAsync` / `SaveSettings` / `ThreeDOnSetting` / `ResetThreeDSettings` / `BuildThreeDSettingsJson` 全部接入（壁纸共用）。
  - `index.html`：「画面与背景」新增 `#set-lyricmode` 分段控件；`chrome.js` 绑定点击 → `onLyricMode` + `persist("lyricMode")`，`applySettings` 回填选中态。
  - `cover.js`：新增 `setLyricText(text)`；掩膜优先级 **歌词行 > 封面图 > 歌名**（`refreshCover`），封面纹理改为缓存复用（`imageTexture`）不再互相 dispose。
  - `app.js`：跟踪 `lyricLines` / `lyricIndex` / `lyricMode`；`syncLyricsUi()` 统一决策——默认模式显示 3D 歌词文字、粒子模式把当前行交给 `cover.setLyricText`，两者按「有歌词且开启」决定歌词按钮态与 now-playing 显隐。
- **已验证**：注入 `settings{lyricMode:"particle"}` + 歌词（index=1）+ 无封面 `artwork` 后截图，粒子云构成当前行「夜空中最亮的星」且 3D 歌词文字层隐藏（临时注入已移除）；`node --check` 通过；编译 0 error、测试 26/26。

### 修复：退出 3D 后 GPU 仍高（2026-09-19）
- 现象：退出 3D 模式后 GPU 占用居高不下。
- 根因：退出时只调用了 `_threeDHost.Stop()`（停掉 C# 推送计时器），**WebView2 页面内的 `requestAnimationFrame` 循环仍在跑**；`ThreeDView` 只是 `Visibility.Collapsed`，Chromium 仍持续渲染 → GPU 不降。
- 修复：
  - `web/app.js` 新增宿主门控 `active`：`frame()` 首行 `if (!active) return;`（不再排下一帧）；`setActive()` 恢复时重置计时基准避免大 `dt`；处理 `{type:"active",active}` 消息。
  - `ThreeDHost` 新增 `PushActive(bool)`；`Stop()` 先 `PushActive(false)` 再停计时器；`StartAsync()` 末尾 `PushActive(true)`；`ready` 握手按 `IsRunning` 回推状态（覆盖“先停后加载”的竞态）。
  - 壁纸宿主独立实例、不受影响（壁纸本就应持续渲染）。
- **已验证**：进入 3D 后自动点击「退出 3D」，WebView2 进程 CPU 3 秒采样由 `0.62s → 0.02s`（约 30×，渲染循环已冻结）。

### 新增：形态「无限」= 隧道 + 螺旋反馈（2026-09-19）
- 需求：一种"无限延伸且不断变化"的播放效果；先做隧道 + 螺旋融合版。
- 实现：
  - 新增 `web/feedback.js` 的 `FeedbackPass`（`Pass` 子类，双 `HalfFloat` ping-pong RT，`needsSwap`）：每帧把上一帧结果按 `uZoom` 放大取样（内容向心收缩=前进）、`uRot` 旋转（螺旋）、`uDamp` 衰减，`uChroma` 各通道微偏移做色散；`max(cur, echo)` 合成避免亮度累加爆白；`ring` 环形遮罩去掉边缘与被"消失点"处的堆积。
  - 接入 `scene.js`（`mainPass → feedback → bloom → overlay → outputPass`，默认关闭），暴露 `setFeedbackEnabled()` 与 `updateFeedback(a, dt)`：`bass`→进速、`beat`→冲击、`treble`→旋转抖动；速率按 `dt` 归一化与帧率无关。
  - 形态列表新增第 6 项「无限」(`COVER_PRESETS` + `index.html data-p="5"`)，`presetIndex===5` 时开启反馈（持久化复用既有 `coverPreset`，无需新设置项）；`reduceMotion` 下不启用。
- 调参要点（踩坑记录）：朴素相加反馈增益 `1/(1-damp)`，`damp→0.99` 时整屏爆白 → 改用 `max` 合成；`max` 下缩放太慢（0.5/s）回声几乎重合看不出隧道 → 提到约 2.4/s 才形成清晰径向streak/螺旋。
- **已验证**：`XSTAR_3D_CAPTURE` 截图显示粒子/星河/歌词被拉成向心螺旋 streak 的隧道效果，无爆白；`node --check` 通过；编译 0 error、测试 26/26。

### 升级：形态「无限」= 镜头无限推进（2026-09-19）
- 需求：把「无限」从纯后处理隧道升级为**镜头持续向前推进**的感觉。
- 实现：
  - `cover.js` 新增**无限走廊**：`setCorridor(count, spacing)` 以共享 geometry/material 复制 N 份粒子云作为 `group` 子节点（继承同一旋转/缩放/音频响应），按黄金角在飞行轴周围螺旋错开（x/y 偏移 + `rotation.z`），避免从正中心穿过；`advanceCorridor(dt, speed, wrapZ)` 每帧沿 +Z 推进，越过 `wrapZ` 即回收至队尾（跨度为 `N*spacing`），实现无缝无限。
  - `starfield.js` 新增 `advance(dt, speed)`：所有星点沿 +Z 推进并在 `±90` 环绕回收，形成星流掠过的视差。
  - `app.js`：`applyInfinite(on)` 统一开关（`setFeedbackEnabled` + 建/拆走廊），`presetIndex===5 && !reduceMotion` 时启用；每帧 `speed = 6 + pulse*9 + beat*16`，驱动走廊与星流；保留反馈 Pass 形成拖影 streak。
- **已验证**：`XSTAR_3D_CAPTURE` 截图（真实歌曲「野草」播放中）显示多张封面粒子云在 3D 空间中错开后退、构成行进中的走廊，配合星流与拖影，读作镜头持续推进；`node --check` 通过；编译 0 error、测试 26/26。

### 重构：形态「无限」= 无垠宇宙漫游（2026-09-19）
- 需求：不再局限于"封面走廊"，重构成"在无垠宇宙中漫游"的视角。
- 实现：新增 `web/cosmos.js`（按需创建 / 销毁不可见时不占位）：
  - **星云背景**：9 个程序化云团 Sprite（叠加混合，随机缩放 120–280、`NEBULA_SPAN=460` 环绕），部分用歌曲 accent 染色；
  - **视差星层**：远/中/近三层 `Points`（1400/900/700）沿 +Z 以不同速率（0.35/0.7/1.5）流动并环绕 → 强视差；
  - **近景尘埃**：NormalBlending 的细颗粒层，掠过感；
  - `advance()` 推进所有层，`update()` 随 `level` 变亮、整体缓慢自转，`setAccent()` 同步配色，`setHeight()` 跟随 DPR/视口。
  - `app.js`：`applyInfinite` 时启用 cosmos 并把封面云**推到 z=-38** 作为"远方的歌之星"（不再做封面走廊）；帧循环 `infiniteActive` 分支改为**漫游相机**——横向/纵向正弦漂移 + `rotateZ` 侧倾 + `beat` 轻微 FOV 冲击；太阳系般的星流速度 `7 + pulse*10 + beat*18`。
  - 反馈拖影在宇宙模式下调低到 0.45（`stage.updateFeedback(a, dt, scale)`），读作"漂流"而非硬隧道；离开该模式恢复 fov=45。
- **已验证**：`XSTAR_3D_CAPTURE` 截图（「野草」播放中）显示紫蓝色星云背景 + 星流拖影 + 远处封面光点 + 轻微侧倾的漫游视角；`node --check` 通过；编译 0 error、测试 26/26。

### 调整：宇宙模式纯黑虚空 + 隐藏封面粒子（2026-09-19）
- 需求：宇宙漫游时前方不应再有封面；背景应为黑色。
- 改动：
  - `app.js` 新增 `syncCoverVisibility()`：`infiniteActive` 时 `cover.group.visible=false`（整团粒子云含"粒子歌词"一并隐藏，纯宇宙）；离开即恢复。
  - 新增 `applyBackground()`：宇宙模式下强制背景 `#000000` 且清除背景图；离开时用缓存的 `bgColorSetting`/`bgImageSetting`（来自 `applySettings`）还原用户设置。不再把封面推到 z=-38。
  - `cosmos.js` 星云弱化：9→6 个 Sprite、opacity `0.16~0.32 → 0.05~0.12`、尺寸 120~280→90~210、半径外推，让黑色基底占主导，仅保留少量氛围。
- **已验证**：截图显示纯黑背景 + 彩色星流拖影，无任何封面/粒子云；`node --check` 通过；编译 0 error、测试 26/26。

### 重构：形态「赛博」= 赛博朋克夜城（2026-09-19）
- 需求：宇宙漫游效果不好看，改为夜晚赛博朋克城市：霓虹随节拍脉动、全息均衡器条从地面升起、重低音冲击波、飞车光轨、湿街反射、电影级光照、动态镜头。
- 实现：删除 `cosmos.js`，新增 `web/cybercity.js`（全程序化，无外部资源）：
  - **楼群**：120 栋塔楼（共享 `BoxGeometry` + 自定义 `ShaderMaterial`），窗口按**世界坐标**网格排布（缩放无关），随机亮灭/闪烁；per-tower 环境微光 + `uNeon`(歌曲 accent) 染色 + `uPulse` 随音量；自带指数雾（`uFog` 紫、`uFogDensity`）。
  - **霓虹**：34 块加法混合招牌（程序化 canvas 图）+ 80 个霓虹光点 Sprite + 高楼顶灯；各自相位脉动并受 `beat` 冲击。
  - **湿街反射**：深色沥青网格地面 + 招牌下方的**地面拖影**贴片（加法混合、随招牌脉动）+ 大范围地面光晕 `spill`。
  - **全息均衡器**：48 根加法条从地面（`GROUND_Y`）升起，高度由 `spectrum` 分频驱动、发光基线。
  - **重低音冲击波**：5 个环形地面波池，`beat>0.55` 触发扩散淡出。
  - **飞车光轨**：6 辆小车（车体 + 长条拖影）横穿天空并随城市回收。
  - **雨**：1400 点竖条贴图下落循环，跟随镜头。
  - **地平线辉光**：远景竖直渐变面（紫→品红）营造天际线氛围。
  - `app.js`：`applyInfinite` 启用城市、隐藏封面云、背景强制黑、**关闭反馈拖影**、曝光提到 1.75（离开恢复 1.3）；帧循环改为**电影感运镜**——沿街推进 `10 + pulse*11 + beat*18`、横摆/起伏/微侧倾、`beat` FOV 冲击；`setAccent` 同步城市霓虹。形态标签 `无限 → 赛博`。
  - `scene.js` 新增 `setToneExposure()`。
- **已验证**：`XSTAR_3D_CAPTURE` 截图（「不讲道理」播放中）显示紫粉天际线辉光、发光窗格楼群、霓虹光点、地面网格与光晕、全息均衡器条；`node --check` 通过；编译 0 error、测试 26/26。
- 遗留：`feedback.js` 与 `cover.js` 走廊代码暂时保留但已不启用（后续可能复用）。

### 新增：形态「虫洞」= 第一人称超空间跳跃（2026-09-19）
- 需求：第一人称穿越宇宙虫洞、超空间跳跃、星光条纹变彩色丝带、穿过多色星云（紫→青→金）、远处抽象几何形变、电影级光照、高速、运动模糊、沉浸。
- 实现：新增 `web/wormhole.js`（程序化）：
  - **虫洞通道**：`CylinderGeometry(6,6,560,80,1,true)` 内壁 + 自定义 Shader（`BackSide` + 加法混合）：纵向 **ribs**（`fract(v*70 - uScroll)` 形成向镜头滚动的环脊）+ 沿轴 **ribbons**（`pow(|sin(u*84π)|,14)` 长条丝带）+ twist 调制；`uScroll` 由实际里程累加，速度变化真实。
  - **调色**：HSL 色相环插值（紫 0.76 → 蓝 → 青 0.5 → 品红 → 金 0.12 → 紫），避免 RGB 直插穿绿；Shader 与 JS 各一份 `palette()` 保持一致。
  - **星云**：10 个加法 Sprite 沿通道内侧漂移回收、颜色按色相环循环、闪烁。
  - **抽象几何**：5 个线框体（Icosahedron/TorusKnot/Octahedron）顶点位移形变（`sin/cos` 噪声）+ 自旋 + 颜色循环 + 随脉冲放大，沿 Z 高速掠过回收。
  - **通道尽头核心光** + 随节拍脉动。
  - **运动模糊/光带**：`stage.setFeedbackEnabled(true)` 复用反馈 Pass。
- `app.js`：重构为 `applyPresetMode()` 统一管理两个替换场景 `赛博(CYBER_PRESET=5)` / `虫洞(WORMHOLE_PRESET=6)`：`cyberActive`/`wormholeActive`；背景模式强制黑；曝光 虫洞 1.65 / 赛博 1.75；虫洞隐藏星空（`stars.points.visible=false`）与封面云。
- 相机：第一人称、`fov 72 + beat*8`、高速 `45 + pulse*60 + beat*95`、横摆+滚转+微摆动。
- **已验证**：截图显示紫色通道 + 彩色丝带放射 + 中心变形的线框几何；`node --check` 通过；编译 0 error、测试 26/26。形态列表新增「虫洞」（index 6）。

### 新增：形态「极简」= 极简 3D 粒子波场（2026-09-19）
- 需求：白/蓝粒子在虚空中漂浮、随声波形成波浪图案、干净背景、柔和光晕、优雅运动、抽象艺术、舒缓但动态。
- 实现：新增 `web/minimal.js`（程序化）：
  - **波场**：`gridSize²` 网格 `Points`（balanced≈96²，随质量档 64~180），顶点着色器用 4 组正弦/径向波叠加生成 y 位移——低频 `pulse` 抬升大涌浪、`mid` 中波、`treble` 细纹、径向扩散波；波峰冷白、波谷深蓝（`mix`）。
  - **柔和光晕**：片元用柔和圆盘 `smoothstep` 双层（核心+光晕）× 加法混合 + bloom。
  - **星空**：1100 个白/蓝漂移星点（顶点内正弦漂移）。
  - **优雅运动**：整体缓慢自转 `0.02 rad/s`；镜头低速环绕（半径 58、y≈24、fov 32）俯视。
- `app.js`：新增 `MINIMAL_PRESET=7` 与 `minimalActive`，并入 `applyPresetMode()`：强制黑背景、隐藏封面云与基础星空、曝光 1.3；`applyViewport` 同步 `setHeight`。
- **已验证**：截图显示黑底虚空中漂浮的白/蓝粒子波场（波峰波谷分层）+ 稀疏星点；`node --check` 通过；编译 0 error、测试 26/26。形态列表新增「极简」（index 7）。

### 极简：支持视角与粒子设置（2026-09-19）
- 需求：与「律动」一致，极简模式可调视角与粒子参数。
- 改动：
  - **视角**：`app.js` 极简分支改用共享 `orbit`（拖拽旋转 / 滚轮缩放），叠加轻微自动漂移；滚轮缩放范围按模式区分（极简 `22~140`，其余 `4.5~12`）；首次进入极简设默认视角（`theta 0 / phi 0.46 / radius 82`）。
  - **粒子设置**：`minimal.js` 新增 `uSizeMul` / `uColorMode` / `uMonoColor` / `uAccent` 与 `setSizeMultiplier` / `setGridMultiplier`（重建网格 48~240）/ `setColorMode`（单色 / 白蓝自然 / 彩色色相）/ `setMonoColor`；`app.js` 的 `onParticleSize/onParticleCount/onColorMode/onParticleColor` 与 `applySettings` 同步作用到极简场与星空层；光晕沿用全局 bloom `glowMultiplier`。
- **已验证**：`node --check` 通过；编译 0 error、测试 26/26。

- **W4 待开始**：锁屏 / 全屏应用暂停、多显示器评估、可选开机自启（`--wallpaper` + 启动项）、纯逻辑单测、`installer.iss` 核对。
