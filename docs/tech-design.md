# XSTAR（Windows 版）技术设计文档

## 1. 文档目的

将《XSTAR（Windows 版）本地音乐播放器需求文档》映射为可落地的 Windows 桌面实现设计，包含技术选型依据、总体架构、模块设计、数据模型、实现路径（Phase 0–4）与风险清单，作为后续开发与验收依据。

## 2. 设计基线

| 项目 | 方案 |
| --- | --- |
| 应用名称 | XSTAR（Windows 版） |
| 语言 / 框架 | C# / .NET 8（Desktop Runtime） |
| UI | WPF（XAML + MVVM，无第三方控件库起步） |
| MVVM 工具 | CommunityToolkit.Mvvm |
| 依赖注入 / 配置 | Microsoft.Extensions.DependencyInjection + 自有 `settings.json` |
| 播放引擎 | BASS（BASS.NET），原生音频引擎 |
| 元数据 / 封面 | TagLib# |
| 封面解码与处理 | WPF `BitmapDecoder` / `PngBitmapEncoder`（缓存目录） |
| 数据库 | SQLite（Microsoft.Data.Sqlite） |
| 库视图 UI | WPF `ListView` 虚拟化 + DataTemplate |
| 图片异步加载 | 自研轻量 CoverImageService（内存 LRU + 磁盘缓存） |
| 在线封面 | iTunes Search API + MusicBrainz（P1） |
| 在线歌词 | LRCLIB（P1） |
| 在线搜歌下载 | 第三方公共聚合服务（同 Android 版策略，P2） |
| 打包 | `dotnet publish` 单文件 + Inno Setup / MSIX（后续） |
| 系统托盘 / 媒体键 | Hardcodet.NotifyIcon.Wpf + Windows APPCOMMAND / SMTControls |
| 项目结构 | `Xstar.Music.sln`，App + Core + Tests |

## 3. 技术选型依据

### 3.1 选型结论

主推 **C#/.NET 8 + WPF**，配套 BASS 播放引擎 + TagLib# 元数据 + SQLite。

### 3.2 候选对比

| 维度 | C#/.NET 8 + WPF（选定） | Kotlin + Compose Desktop | Flutter Desktop | Electron / Tauri |
| --- | --- | --- | --- | --- |
| Windows 桌面生态 | ★★★★★（音乐播放器 OSS、音频库文档最全） | ★★（桌面冷门） | ★★★ | ★★★★ / ★★★（Web 生态） |
| 运行时性能 / 资源占用 | ★★★★（原生，启动快、常驻轻量） | ★★★（JVM） | ★★★ | ★★ / ★★★★ |
| 音频能力深水区 | BASS/NAudio/libVLC 成熟 | 需 libVLC/mpv 绑定，坑多 | 较弱 | 弱（依赖 Web Audio / 原生模块） |
| 与开发者既有技能匹配 | 需学 C#/XAML/MVVM | ★★★★★（完全复用） | 学 Dart | 学 TS/前端 |
| 主流度 | 高（.NET 官方支持 Windows 桌面） | 低 | 中 | 中/高 |

### 3.3 关键取舍

- **选 WPF 而非 WinUI 3**：生态最成熟、无 MSIX/WinAppSDK 附带复杂度、单窗口自定义皮肤自由度最高、全部音频库文档围绕 WPF/.NET Framework 演进而来。WinUI 3 留作未来“现代外观”升级路径；UI 逻辑通过 ViewModel 隔离，便于迁移。
- **BASS 而非自研解码或 NAudio 全自建管线**：BASS 原生性能高、解码体积小、格式覆盖广（含 FLAC/OGG/Opus/WMA），内建 10 段 EQ DSP（BASS_FX）、无缝播放与 WASAPI/ASIO 输出能力，成熟度被大量桌面播放器验证。许可：个人/非商业免费，商用需向 Un4seen 购买授权（本项目现阶段适用免费条款）。
- **TagLib# 而非自研解析**：与 Android 版 `MediaMetadataRetriever` 对位，负责标签/时长/内嵌封面提取；位深等参数以各容器解析（FLAC/ALAC/WAV 可靠），个别格式缺失时按 Android 同款降级规则处理（默认值 + 重新扫描后更新）。
- **SQLite 直用而非 ORM**：扫描为高吞吐批量 upsert，原生 `Microsoft.Data.Sqlite` 最简单可控，避免 EF Core 扫描路径的性能与迁移噪音；表结构贴近 Android Room 模型。
- **不选 Kotlin/Compose Desktop**：你的诉求是“生态 + 性能 + 主流”，Kotlin 桌面生态不满足；尽管其能复用你现有 Compose/Android 代码。若未来反悔，可整体转向 Compose Desktop + libVLC(vlcj)，需求与数据模型不变。

## 4. 总体架构

```text
WPF View（XAML + DataTemplate）
   │  Command / INotifyPropertyChanged（单向绑定）
MusicViewModel / PlayerViewModel / LibraryViewModel / SettingsViewModel
   │
   ├───────────────┬──────────────────┬──────────────────┐
MusicService   LibraryService     CoverService     SettingsService
   │               │                  │
PlayerEngine(I)  ScanEngine       LyricService     OnlineMusicService(P2)
   │  (BASS)       (TagLib#+SQLite)   (LRCLIB P1)      (聚合 API P2)
   │
BASS_FX DSP        Microsoft.Data.Sqlite + settings.json
```

设计要点：
- 所有状态经 ViewModel 暴露为 `ObservableObject`，View 不做业务逻辑。
- `PlayerEngine` 接口抽象 BASS，便于未来替换/测试；UI 只依赖该接口。
- `LibraryService` 暴露可观察的 `SongSource`（含歌曲、专辑、艺术家聚合），播放列表与库视图复用同一数据源。
- 扫描、封面提取、在线请求全部在 Task/后台线程，结果经 `Dispatcher` 回到 UI。

## 5. 项目结构

```text
Xstar.Music.sln
├── src/
│   ├── Xstar.Music.App/          # WPF 可执行程序
│   │   ├── App.xaml(.cs)         # DI、全局异常、单实例
│   │   ├── MainWindow.xaml(.cs)  # 单页布局（顶栏+专辑区+底部面板）
│   │   ├── Views/                # UserControl：TopBar、NowPlaying、QueuePanel、LyricsPanel、Dialogs
│   │   ├── Controls/             # QualityBadge、CoverBox、TrackProgress 等
│   │   ├── Themes/               # 深色主题、资源字典、样式
│   │   └── ViewModels/
│   │       ├── MainViewModel.cs
│   │       ├── PlayerViewModel.cs
│   │       ├── LibraryViewModel.cs
│   │       └── SettingsViewModel.cs
│   ├── Xstar.Music.Core/         # 与 UI 无关的领域与数据层（net8.0）
│   │   ├── Models/               # Song, Album, Artist, PlayQueue, Quality...
│   │   ├── Audio/
│   │   │   ├── IPlayerEngine.cs  # 播放抽象
│   │   │   ├── BassPlayerEngine.cs
│   │   │   ├── Equalizer10Band.cs# BASS_FX 10 段 EQ 封装（P2）
│   │   │   └── QualityClassifier.cs
│   │   ├── Data/
│   │   │   ├── XstarDbContext.cs  # 轻量 DataReader 封装 + 迁移
│   │   │   ├── SongRepository.cs
│   │   │   ├── FolderRepository.cs
│   │   │   └── Migrations.cs
│   │   ├── Scanning/             # ScanEngine、增量 FileSystemWatcher
│   │   ├── Meta/                 # TagLib 读取封装、CoverExtractor
│   │   ├── Lyrics/               # LrcParser、LyricService
│   │   ├── Online/               # (P2) OnlineMusicProvider、聚合客户端
│   │   ├── Settings/             # SettingsService（settings.json）
│   │   └── Services/
│   │       ├── PlaybackSession.cs# 现场保存/恢复（queue/index/position/mode）
│   │       └── MediaKeys.cs      # APPCOMMAND / SMTControls
│   └── Xstar.Music.Tests/        # xUnit：Quality、LrcParser、稳定ID、Repository
├── docs/
│   ├── requirements.md
│   ├── tech-design.md
│   └── ui-prototype.html
└── Xstar.Music.sln
```

## 6. 数据模型

### 6.1 settings.json（`%APPDATA%\XstarMusic\`）

```text
folders: [ { path, addedAt } ]             # 已添加音乐目录
window:   { x, y, w, h }                   # 窗口记忆（P1）
playbackMode: Sequential|Shuffle|RepeatOne
wasPlaying: bool
queue:    [ { songId, ... } ]              # 现场快照（约1s debounce）
onlineArtworkEnabled / onlineLyricsEnabled / onlineSearchEnabled: bool（默认 true）
downloadFolder: string?                    # P2
onlinePlatform: string                     # P2
eqEnabled: bool, eqGain: float, eqBands: float[10], eqPreset: string   # P2
theme: 'deep'|...                          # 预留
```

### 6.2 SQLite `xstar.db`（版本 1）

```text
songs:
  songId          TEXT PRIMARY KEY     # SHA-256(path) 前 16 hex
  path            TEXT NOT NULL        # 文件绝对路径
  folderId        TEXT                 # 所属目录（冗余便于清理）
  title, artist, album    TEXT
  durationMs      INTEGER
  codec           TEXT                 # MP3/AAC/FLAC/ALAC/...
  bitrate         INTEGER              # bps
  sampleRate      INTEGER              # Hz
  bitDepth        INTEGER
  channelCount    INTEGER
  quality         TEXT                 # Standard/High/Lossless/HiRes
  fileName        TEXT
  fileSize        INTEGER
  dateModified    INTEGER
  artKey          TEXT                 # 缓存封面文件名（songId 或 album 摘要）
  hasEmbeddedArt  INTEGER
  isFavorite      INTEGER
  playCount       INTEGER
  lastPlayedAt    INTEGER
  onlineId        TEXT                 # P2: 平台:歌曲ID

folders:
  id TEXT PRIMARY KEY, path TEXT UNIQUE, addedAt INTEGER

playlists (P1):      id, name, createdAt, updatedAt
playlist_songs (P1): playlistId, songId, position, addedAt
```

- Album/Artist 不单独建表，由 `songs` 聚合（与 Android 一致）。
- 歌曲稳定 ID 规则与 Android 版一致：完整路径 SHA-256 摘要。
- 数据库升级提供显式迁移，禁止 destructive migration。

## 7. 播放设计（BASS）

### 7.1 PlayerEngine 接口（能力对齐需求）

```text
LoadQueue(songs, startIndex)
SetMode(Sequential/Shuffle/RepeatOne)
Play/Pause/Next/Previous/Seek(ms)/Toggle
RemoveAt(index)/Clear()
Position/Duration/IsPlaying/Index/Error   → 事件或 INotifyPropertyChanged
EndOfTrack 事件 → 由引擎按模式触发下一首或停止
```

### 7.2 模式实现

| 应用模式 | 实现 |
| --- | --- |
| Sequential | 播放到队列末尾停止 |
| Shuffle | 维护随机取样的循环访问顺序（Fisher–Yates，跳过当前） |
| RepeatOne | 单曲结束重新定位到 0 播放 |

### 7.3 引擎细节

- 使用 BASS `BASS_StreamCreateFile` 按文件播放；切换下一首前 `BASS_ChannelStop` 并释放句柄。
- 播放失败（`BASS_ErrorGetCode` 非可解码/文件损坏）映射为可读文案并自动跳过。
- EQ（P2）：`BASS_ChannelSetFX(ch, BASS_FX_BFX_PEAKEQ)` 组建 10 段（31Hz/62/125/250/500/1k/2k/4k/8k/16kHz）+ 前置增益；EQ 开关对应 `BASS_ChannelRemoveFX` 旁路。
- 音频输出选择 WASAPI（共享），Hi-Res 无损按需可研究独占路径（P2）。
- 进度上报低频率（约 250ms）驱动 UI，避免高频绑定。

## 8. 扫描设计

```text
添加文件夹(FolderBrowserDialog)
  → 持久化到 settings.folders
  → 触发 ScanEngine.RunAsync(folders, cts)

ScanEngine：
  WalkDirectory（并行受限遍历）
    扩展名白名单: .mp3 .m4a .aac .flac .ogg .opus .wav .wma
    TagLib# 读取 tags/时长/位深等
    CoverExtractor 提取内嵌封面 → 缓存目录 album/{artKey}.jpg
    QualityClassifier 分级
    upsert songs（保留 isFavorite/playCount/lastPlayedAt）
  ─────────────────────────────────────────────
  IndexCleanup：删除不在有效范围内的 songId（不删除磁盘文件）
  进度：已扫描文件数 / 总数，支持取消
```

- 单文件解析失败仅记录日志并跳过。
- P1 增量：启动或手动重扫后为各目录挂 `FileSystemWatcher`，事件去抖（约 2s）后增量处理变更文件。
- 目录集合为空时清空索引、UI 显示添加引导。

## 9. 封面设计

- 内嵌封面 → `album/{artKey}.jpg` 磁盘缓存；`artKey` 优先 `songId`（按文件）或专辑摘要（按专辑共享，去重存储）。
- `CoverService`：内存 LRU + 异步解码 + 无封面时默认图（内置 SVG/生成占位）。
- P1 在线补充：用户触发 → iTunes Search（`entity=album`, `country=cn`→`us` 回退，`artworkUrl100`→`600x600`）或 MusicBrainz → 候选网格 → 选中落缓存并更新 `artKey`。
- 默认封面按专辑/标题生成确定色渐变，避免大量重复默认图视觉单调（原型中已体现该风格）。

## 10. 桌面集成

- 任务栏：系统缩放按钮（`WindowChrome`）+ 任务栏缩略图按钮 `TaskbarItemInfo.ThumbButtonInfo`（播放/暂停、上下首）。
- 媒体键：优先走 `TaskbarItemInfo` 与系统 `WM_APPCOMMAND`（APPCOMMAND_MEDIA_*）；必要时通过隐藏窗口 + HwndSource hook 捕获。全局后台媒体键可后续评估 SMTControls 互操作。
- 托盘：Hardcodet.NotifyIcon.Wpf（播放/暂停、上下首、显示/隐藏、退出）；关闭到托盘可选。
- 单实例：命名 Mutex 二次启动激活已有窗口。

## 11. 在线歌词（P1）

来源优先级同 Android 版：

```text
%APPDATA%\XstarMusic\lyrics\{songId}.lrc
  ↓ 没有
音频同目录同名 .lrc
  ↓ 没有
歌词面板显示“获取歌词” → LRCLIB /api/get → /api/search → 用户选择 → 保存
```

`LrcParser` 支持 `[mm:ss.xx]`、`[mm:ss:xxx]`、一行多时间标签、UTF-8 BOM 与纯文本；当前行定位基于播放位置，`ScrollViewer`/`Virtualizing` 自动滚动到高亮行。

## 12. 在线搜歌下载（P2）

沿用 Android 版已验证策略：

```text
聚合 API 搜索（平台可切换：netease/qq/kugou/kuwo/migu…）
  → 候选列表（标题/歌手/专辑/平台）
  → 用户选下载 → 解析最佳可用音质直链
  → 流式下载到用户目录（进度/取消/重试）
  → TagLib# 分析真实格式并抓取封面
  → 写入 songs（onlineId 标记）→ 自动出现在库中
```

- 复用现有公共聚合服务方案（默认实例可在设置中替换）。
- 仅个人学习与本地使用，遵守版权与平台条款；同 Android 版的限制认知直接迁移。

## 13. UI 设计（对应 ui-prototype.html）

单页布局（详见 `ui-prototype.html` 交互原型）：

```text
顶栏：窗口标题区 + 音乐库筛选（全部/专辑/艺术家）+ 搜索 + 文件夹/扫描 + EQ + 设置
上方专辑区：方形封面（播放时旋转唱片效果）+ 曲名/歌手/专辑/音质徽标 + 进度条 + 控制条
底部面板：Tab[播放列表 | 歌词]，可拖拽调整高度
```

- 深色主题、玻璃拟态与品牌渐变，风格对齐产品线（深色蓝紫品牌色）。
- 数据模板展示 Title/Artist/Album/Duration/Format/QualityBadge；行内收藏按钮。
- 快捷键：空格 播放/暂停、←/→ 进度、↑/↓ 音量（预留）。

## 14. 实现路径

### Phase 0 · 技术验证（0.5–1 周）

目标：用最小 demo 一次性验证关键技术风险。

任务：
- 建立 `Xstar.Music.sln` 骨架（App + Core + Tests），WPF 单窗口 + 深色主题。
- BASS 播放 mp3/flac + 进度/暂停/切歌；BASS 10 段 EQ 滑杆实时生效。
- TagLib# 读标签 + 内嵌封面；SQLite 建表与万行批量 upsert 计时。
- `dotnet publish` 单文件 + bass.dll 布局与运行。

退出标准：一个可运行的“添加文件夹 → 扫描入库 → 点击播放 → 拖 EQ”最小 demo，且记录各格式（含 Hi-Res FLAC）可播放性。

### Phase 1 · MVP（约 2–3 周）

- 文件夹管理（添加/移除/扫描/进度/取消）、元数据+封面+音质入库。
- 单页 UI（顶栏+专辑区+底部队列面板）、播放控制、三模式、队列操作。
- 播放现场持久化/恢复、媒体键、任务栏缩略图、托盘。
- xUnit 单元测试（Quality、稳定 ID、Repository upsert/清理）。

退出标准：真实千级曲库“多目录→扫描→浏览→播放→重启恢复”全链路稳定，无主线程卡顿。

### Phase 2 · 封面/歌词增强（约 1.5–2 周）

- LRC 解析与歌词面板（本地同名 LRC → 缓存 → LRCLIB），当前行高亮滚动。
- 在线封面/歌手图候选与落缓存；`FileSystemWatcher` 增量扫描。
- 播放列表功能（P1 可裁剪）。

退出标准：覆盖需求文档歌词/封面用例（多时间标签、BOM、无结果、开关关闭不请求等）。

### Phase 3 · EQ 与在线搜歌（约 2–3 周）

- 10 段 EQ + 前置增益 + 预设 + 直通旁路（BASS_FX）。
- 在线搜歌面板、下载入库、`onlineId` 去重、失败/重试/取消清理。

退出标准：EQ 预设记忆可用；下载即播、重复拦截、取消无残留。

### Phase 4 · 打磨与发布（约 1–2 周）

- 万级/十万级曲库性能压测与虚拟化调优。
- 安装包（单文件 + Inno Setup 安装器或 MSIX），自动更新可选。
- 崩溃恢复、日志、界面细节打磨。

## 15. 风险清单

| 编号 | 风险 | 缓解 |
| --- | --- | --- |
| R-001 | BASS 商用授权边界 | 非商用免费；若商用需购买授权，或评估切换 libVLC(MPL/LGPL)/自研管线 |
| R-002 | TagLib# 部分格式位深/参数读取不全 | FLAC/ALAC/WAV 可靠；缺失时默认值并在重扫后更新，必要时针对容器补充解析 |
| R-003 | 后台全局媒体键捕获在 WPF 中受限 | Phase 1 先保证窗口聚焦态快捷键+任务栏缩略图；后台媒体键再评估 SMTControls 互操作 |
| R-004 | 大目录扫描 IO 阻塞 | 受限并行 + 进度/取消 + 增量 FileSystemWatcher；万级扫描不得卡 UI |
| R-005 | bass.dll/x64 分发与杀软误报 | 使用官方 runtime 并按目录整理，文档记录；发布前验证 |
| R-006 | WPF 高分屏/缩放/DIRECT2D 渲染兼容 | 高 DPI 感知 + PerMonitorV2 配置，关键设备清单测试 |
| R-007 | 播放现场恢复竞态（快速退出/切歌中崩溃） | debounce 快照 + 启动时校验 songId 有效性，失效即过滤 |
| R-008 | 在线聚合服务可用性/版权 | Provider 抽象 + 可配置地址；仅学习用途提示与 Android 一致 |
| R-009 | EQ 品质预期 | 明确 EQ 为软件 DSP，非源文件修改；Hi-Res 独占输出作 P2 研究项 |

## 16. 测试设计

### 16.1 单元测试（xUnit）

- QualityClassifier：128k/320k MP3、16/44.1 FLAC、24/96 FLAC、WAV、未知 → 对应分级。
- LrcParser：两种时间标签、一行多标签、BOM、纯文本、乱序时间排序。
- 稳定 ID：同路径同 ID、不同路径不同 ID、确定性。
- SongRepository：upsert 保留收藏/计数、目录移除清理、无 destructive 迁移。

### 16.2 手工测试重点

- 两个以上文件夹、子目录与非音频文件混合扫描；重启后目录保留。
- 中文/空格/括号文件名；无标签文件。
- 各格式实际播放与进度拖动；损坏文件跳过。
- 三种模式行为；队列删除/清空/末尾。
- 重启恢复队列与进度；媒体键/缩略图/托盘。
- 封面：有/无内嵌、在线多候选、超时、开关关闭不发请求。
- 歌词：同名 LRC、多时间轴、LRCLIB 多候选、纯文本、开关行为。
- 大库滚动与搜索流畅性。

## 17. 文档同步约定

`docs/requirements.md`、`docs/tech-design.md` 与 `docs/ui-prototype.html` 应随开发同步维护；新增功能需同步更新需求、设计落点、数据迁移、测试场景与风险。

## 18. 当前实现状态

当前仓库已完成第二轮功能实现，并已通过编译、单元测试与启动验证：

- 已创建 `Xstar.Music.sln`、`Xstar.Music.App`、`Xstar.Music.Core` 与 `Xstar.Music.Tests`。
- 已实现单窗口深色界面、顶部专辑背景、歌曲标题/歌手/音质展示、播放控制、进度条、音量弹层。
- 已实现歌曲信息弹层、播放列表与歌词面板、浏览方式（歌曲/专辑/艺术家/我的收藏/最近播放/播放列表）与歌词样式菜单。
- 已实现多目录配置的 JSON 持久化、递归扫描、支持格式过滤和 SQLite 歌曲索引。
- 播放后端已接入 **BASS/BASS.NET**：`BassPlayerEngine` 实现 `IPlayerEngine`，原生 `bass.dll` 与 FLAC/Opus/ALAC/APE 插件位于 `src/Xstar.Music.App/native/win-x64` 并随构建复制到输出目录；初始化失败时自动回退到 `MediaPlayerEngine`（WPF `MediaPlayer`）。UC 端仍仅依赖接口。
- 已实现收藏、播放历史、自定义播放列表（`favorites` / `play_history` / `playlists` / `playlist_items` 表，`PRAGMA user_version = 3`）。
- 已实现本地同名 LRC 解析、逐行高亮、字号/淡化样式持久化，以及 **LRCLIB 在线歌词**获取与本地缓存（`%APPDATA%\XstarMusic\lyrics`）。
- 已实现基于 **Jamendo**（Creative Commons 免费音乐）的在线搜索与下载，需在设置中填写免费 `client_id`；下载目录可配置。
- 已实现全局媒体键（播放/暂停、上一首、下一首、停止）与系统托盘（显示、控制、退出），关闭窗口时最小化到托盘。
- 已验证：`dotnet build .\Xstar.Music.sln` 为 0 警告 0 错误，`dotnet test .\Xstar.Music.sln` 17/17 通过，应用可正常启动并初始化 BASS 2.4。
- 剩余：BASS 10 段 EQ、无缝/淡入淡出播放、在线音源多源聚合与音乐视频等 P2 能力；EQ 依赖 BASS_FX 插件。

## 19. 第三方依赖与许可

- **BASS / BASS.NET**：BASS 原生库（`bass.dll` 及插件）由 Un4seen Developments 提供，非商业用途免费；许可证文本见 `src/Xstar.Music.App/native/win-x64/BASS-LICENSE.txt`。商业化前需评估授权或切换 `IPlayerEngine` 到 libVLC 等替代实现。
- **ManagedBass**（NuGet）：MIT 许可的托管绑定。
- **TagLib#**、**Microsoft.Data.Sqlite**：各自开源许可。
- **Jamendo API**：需自行申请 `client_id`，使用须遵守其服务条款。
- **LRCLIB**：公开歌词接口，用于在线歌词获取。

验证命令：

```powershell
dotnet build .\Xstar.Music.sln
dotnet test  .\Xstar.Music.sln
dotnet run   --project .\src\Xstar.Music.App\Xstar.Music.App.csproj
```
