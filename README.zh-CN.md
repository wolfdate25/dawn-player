# Dawn Player

<p align="right"><a href="README.md">English</a> · <a href="README.ko.md">한국어</a> · <b>简体中文</b></p>

一款受 foobar2000 的功能性以及 [Eole 主题](https://github.com/Ottodix/Eole-foobar-theme)设计启发的
原生 Windows 音乐播放器（WinUI 3 / .NET 10）。

![release](https://img.shields.io/github/v/tag/wolfdate25/dawn-player?label=release&color=blue)

![Dawn Player Screenshot](docs/screenshot.png)

## 下载

可从 [GitHub Releases](https://github.com/wolfdate25/dawn-player/releases/latest) 下载最新的
安装程序或便携 ZIP（SHA256 校验和一并发布）。需要 Windows 10 19041 或更高版本。

- **安装程序（`.exe`）** — 支持按用户（默认）或按机器安装；自动注册快捷方式、开机启动、
  音频文件关联（.mp3、.flac、.wav、.m4a 等）和资源管理器右键播放菜单；会检测正在运行的
  实例并安全地升级/卸载
- **便携包（`.zip`）** — 解压即用，无需安装

数据保存在 `%AppData%\DawnPlayer`（settings.json、library.db、playlists/、artcache/、
dawnplayer.log）。**便携模式**：exe 旁存在 `portable.dat` 标记时，使用运行目录下的 `data\`
而非 `%AppData%`（便携 ZIP 自带此标记）。

## 主要功能

### 音频引擎
- **WASAPI 独占输出** — 位完美（bit-perfect）直接输出。当设备不支持当前格式或其他应用占用
  设备时，自动回退到共享模式（并显示通知）
- **无缝播放（Gapless）** — 音轨边界以采样精度衔接的序列器。提前打开下一首的解码器
  （预取），无间断衔接
- 独占模式的采样率不匹配策略 — 可选择在曲目边界重建输出会话（位完美），或保持会话运行并
  重采样（无缝衔接）
- **网络电台** — 播放 Icecast/Shoutcast MP3 流（通过 ICY 元数据显示电台/曲目信息）。
  位于网络标签页：收藏电台、添加流 URL，播放栏和 SMTC 显示实时电台/曲目信息。
  标题菜单 → 打开网络流；M3U8 播放列表中的 URL 也可直接使用。网络流（电台/DLNA/YouTube）
  在打开或卡顿时会在播放栏显示缓冲状态，中断的流也会明确提示而不是无声地消失
- **DLNA 浏览** — 自动发现网络中的 UPnP 媒体服务器，通过常规引擎播放其音乐（无缝衔接、EQ、
  WASAPI 独占输出全部保留）：选择服务器 → 浏览文件夹 → 立即播放/加入 Now Playing，专辑封面本地
  缓存。优先原始格式（FLAC/WAV/MP3/AAC…）
- **YouTube 播放**（可选）— 在网络标签粘贴视频地址，经 yt-dlp 解析、ffmpeg 管道流入同一引擎。
  依赖检测型：PATH 需有 yt-dlp·ffmpeg。仅限有损音源，且属非官方途径，请仅作个人播放使用
- **支持的格式**：MP3、AAC/ALAC (m4a)、FLAC、Ogg Vorbis、Opus、WAV、DSF/DSD、DFF (DSDIFF) —
  Media Foundation + NVorbis 解码。DSD 通过箱形抽取转换为 44.1k/48k 系列 PCM 播放，或以
  DoP（DSD over PCM）打包，经 WASAPI 独占模式发送至支持 DSD 的 DAC
- **CUE 支持** — 扫描整轨镜像（FLAC/WAV/APE 等）及其 `.cue` 时，按曲目建立虚拟音轨索引，
  曲目级播放、统计与评分均可使用，整轨镜像行会被隐藏（foobar2000 方式）。
  区间播放基于无缝序列器以采样精度衔接
- 独占模式位深策略（原始/16/24/32 位），延迟缓冲调节（30–500 ms）
- **参数均衡器（按设备独立配置）** — 最多 8 段动态滤波器（Peak EQ、Low/High Shelf、
  Low/High Pass）、前置放大（±12 dB）、按设备的独立配置与公共默认配置回退、播放中即时生效
  以及位完美旁路
- **ReplayGain**（曲目/专辑增益、预放大、防削波）— 支持 FLAC/OGG (Xiph) 与 MP3 (ID3v2 TXXX) 标签
- **卷积（脉冲响应）** — 使用 WAV/FLAC 等脉冲文件进行房间/音箱/耳机校正。FFT 分块卷积
  （512 采样块，IR 最长 2 秒）可在播放中实时应用与替换（设置 → 播放 → 空间听感）
- **动态音量归一化（AGC）** — 自动平衡音轨间响度。混合模式优先使用 ReplayGain 标签，否则
  回退到动态 AGC；可调目标电平/最大提升/响应速度，并带静音门限
- **耳机串扰补偿（Crossfeed）**（Chu Moy 相位，弱/中/强）与**单声道下混** — 播放中实时生效

### 播放列表 / 队列（foobar2000 风格）
- 多播放列表（标签页）、重命名、M3U8 自动保存 / 导入 / 导出
- **智能播放列表** — 将播放统计（播放次数/最后播放/跳过）记录到 SQLite，自动生成并刷新
  "播放最多 / 最近添加 / 久未播放"列表（每次播放即时更新，无需配置）
- **基于查询的智能播放列表** — 使用 foobar2000 风格的查询
  （`%rating% GREATER 3 AND %last_played% DURING LAST 30 DAYS LIMIT 50`）直接创建和编辑。
  支持字符串（IS/HAS）、数值（GREATER、>=）、日期（DURING LAST）、MISSING/PRESENT、
  AND/OR/NOT 与括号
- **曲目评分** — 0–5 星。播放列表、资料库表格、正在播放栏中均可点击行内星标单元格直接评分，
  也可通过右键菜单设置（支持多选批量）；资料库表格支持按评分排序；与 ID3v2 POPM / Vorbis·MP4
  RATING 标签双向同步，可作为查询中的 `%rating%` 字段。标签写入失败（只读文件）会以通知提示，
  未评分曲目显示空心星标，便于发现评分入口
- **播放队列** — 按曲目加入队列/插队到最前，以队列优先播放后回到原顺序。
  列表行显示 Q1、Q2… 徽标
- 随机播放、循环（关/全部/单曲）、上一曲历史、**A-B 循环**（音频线程上的采样精度循环，
  进度条上显示循环区间，右键按钮取消，默认快捷键 Ctrl+L）
- 排序（标题/艺术家/专辑·音轨/路径/随机/逆序）、去重、分组切换 + 平铺模式拖拽重排

### 媒体库
- 音乐文件夹索引（SQLite 存储，增量扫描）、艺术家/专辑/流派筛选浏览器 + 搜索
- **WAV 转换器** — 将播放列表曲目导出为 WAV（分割 CUE 镜像、写入 ReplayGain，附带标签与封面）
- **标签编辑器** — 在曲目/专辑右键菜单中编辑元数据与专辑封面，原子化（文件替换）保存
- **ReplayGain 批量分析** — EBU R128 响度扫描器（−18 LUFS）计算曲目/专辑增益，写入标签
  （REPLAYGAIN_*）与数据库并立即用于响度归一化。支持可选写入 **ReplayGain 2.0 标签**
  （R128_TRACK_GAIN / R128_ALBUM_GAIN）及读取回退
- 专辑封面缓存（提取内嵌图片 + cover.jpg 等文件夹封面）
- **封面网格 / 列表视图切换** — 网格磁贴大小调节（Ctrl+滚轮）并持久保存
- **收听报告** — 记录播放历史（play_events），按时间段（全部/最近 30 天/今年）展示总播放、
  收听时长及最常听的艺术家/专辑/流派/曲目（标题菜单 → 收听报告）

### 歌词
- **.lrc 同步歌词** — 自动查找（`音频文件名.lrc`、`艺术家 - 标题.lrc`、`标题.lrc` 及自定义模式），
  当前行高亮 + 自动滚动、点击行跳转、±0.5 秒偏移调整
- **在线歌词插件** — 通过 .NET DLL 插件支持各站点的歌词搜索。可配置插件优先级、
  播放中自动搜索（离线歌词优先）、标题/艺术家/专辑搜索窗口、预览后应用，并可将在线歌词
  保存为离线 .lrc — 支持保存位置（源文件目录/自定义目录）与文件名模板（`%title%` 等变量）
- 自带站点插件（samples 参考实现）：LRCLIB + Alsong — 将构建好的 DLL 复制到插件目录后重新扫描即可启用
- 支持标准/多时间戳/`[offset:]`/扩展（逐字）LRC，自动识别 UTF-8/ANSI
- **LRC 歌词编辑器** — 逐行时间戳编辑/同步、行顺序调整、从剪贴板导入歌词

### 界面（Eole 风格 × WinUI 3）
- 深色优先配色 + Mica 背景、琥珀色强调色，可切换浅色主题
- 多语言界面 — 한국어 / English / 日本語。在 设置 → 外观与主题 中选择语言（支持跟随系统），
  重启后整个界面将以该语言显示
- 带专辑分组头（封面 + 元数据）的播放列表 — Eole 的标志性布局
- 底部播放栏：封面、进度、控制、音量、队列徽标、歌词开关
- SMTC 集成 — 媒体键 / 系统媒体浮层 / 音量弹窗控制
- 17 个快捷键 — 播放控制（`Space` 播放/暂停、`Ctrl+S` 停止、`Ctrl+←/→` 上一首/下一首、
  `Ctrl+H` 随机、`Ctrl+R` 循环、`Ctrl+Shift+S` 播完当前后停止）、导航（`←/→` ±5 秒、
  `Ctrl+B` 回到曲首）、音量（`Ctrl+↑/↓`、`Ctrl+M` 静音）、窗口（`L` 歌词、`Ctrl+F` 搜索、
  `Ctrl+P` 设置）
- 快捷键重映射：在 设置 → 快捷键 中修改所有命令的按键组合（冲突检测 · 单项/全部重置），
  仅将更改保存到 `settings.json`
- **睡眠定时器** — 15/30/60 分钟后或在当前曲目结束时停止播放（标题菜单）
- **迷你播放器** — 标题菜单中的置顶紧凑模式（仅显示播放栏，拖动背景移动窗口，Esc 退出）
- **托盘通知** — 隐藏到托盘时曲目变化会弹出气泡通知（遵循 Focus Assist）
- **Last.fm 记录（Scrobbling）** — 使用自己的 API 密钥进行网页授权并记录播放曲目。
  网络失败时入队重试（标题菜单 → Last.fm）
- **系统托盘集成** — 开启"关闭到托盘"后，关闭按钮将隐藏窗口并保持播放；托盘菜单可控制
  播放/打开窗口/退出；任务栏缩略图工具栏（上一首/播放/下一首）
- 拖放文件/文件夹到播放列表

## 致谢

- [Eole foobar theme](https://github.com/Ottodix/Eole-foobar-theme) — UI 设计参考
- [NAudio](https://github.com/naudio/NAudio) / [NVorbis](https://github.com/njdrummond/NVorbis) — 音频
- [TagLib#](https://github.com/mono/taglib-sharp) — 标签
- foobar2000 — 功能灵感的来源
