# 启动性能优化实施记录

## 背景

这轮优化针对的是桌面端冷启动阶段的主数据路径。

最初观察到的慢启动并不是 Vue 绘制或布局本身导致的，而是
`desktop_state_load_bootstrap` 在应用进入首屏前搬运了过大的曲库数据。
小规模曲库也会被放大成数百 MB 的 JSON，因为每首歌的 `artwork` base64
被存入 `tracks.payload_json`，随后又被复制进旧的
`app_state.snapshot.catalog` 缓存。

这会让冷启动被迫经过：

- SQLite 读取完整曲目 JSON。
- Rust 反序列化完整曲库。
- Tauri IPC 传输巨大 payload。
- Renderer 反序列化和 normalize 完整 tracks。
- Vue store 在首屏前注入全量曲库状态。

这条路径已经偏离了 bootstrap 的职责。启动阶段应该只加载首屏需要的控制面数据，
而不是把媒体数据面整包搬进 renderer。

## 优化目标

本次优化的目标是把启动路径收窄到“首屏可用”的最低数据集：

- 偏好设置、会话状态和播放状态。
- 曲库、歌单、关系和导航计数。
- 当前曲目 ID、队列 ID、最近记录等轻量状态。
- 足够支撑首屏列表查询的 Rust 侧投影缓存。

这些内容不应该进入普通启动 bootstrap：

- 完整曲目列表。
- `artwork` base64。
- 大段歌词文本。
- 文件 blob。
- 远程库浏览结果。
- 任何会随曲库大小线性膨胀的大 payload。

## 实施内容

### 1. Bootstrap 不再返回全量曲目

`desktop_state_load_bootstrap` 现在返回轻量 catalog shell。正常启动下：

- `catalog.tracks` 不再包含全量曲目。
- `catalogTracksIncluded` 为 `false`。
- `catalogTrackCount` 为 `0`。
- `trackCacheEntries` 表示 Rust 侧已预热的曲目投影数量。

前端 store 只接收首屏需要的轻量状态，避免在 visual ready 前 normalize
完整曲目对象。

### 2. 删除旧的 `snapshot.catalog` 缓存

旧版本会把完整 catalog 再复制一份写入 `app_state.snapshot.catalog`。
这会让 SQLite 文件、启动读取量和 renderer payload 同时膨胀。

新版初始化时会清理旧缓存行，并停止继续写入这类全量 catalog 快照。

### 3. Rust 侧使用曲目投影缓存

列表和导航所需的数据改为走轻量投影，而不是解析 `tracks.payload_json`。

Rust 侧负责预热必要的查询缓存，renderer 只在需要完整曲目对象时再发起懒加载。

### 4. 播放和点歌改为按需加载完整曲目

当前播放、点歌和需要完整元数据的操作不再依赖启动时的全量曲库。
当操作需要完整 track 时，通过单曲懒加载补齐。

这样启动耗时不会随着曲库封面大小一起增长。

### 5. 专辑 / 艺术家浏览延后加载

专辑和艺术家浏览属于首屏之后的浏览能力。

它们改为在首屏 ready 后、且视图真正进入相关页面时再加载完整曲库数据，
避免拖慢普通冷启动。

## 观测指标

优化前的典型现象：

- Splash 约 3.2 秒后按超时隐藏。
- 完整启动约 6.3 秒。
- `desktop_state_load_bootstrap` round trip 约 5.3 秒。
- renderer invoke / deserialize overhead 约 2.5 秒。
- Rust 进程 bootstrap 阶段内存上升约 320 MB。
- renderer heap 启动阶段上升数百 MB。

优化后的典型现象：

- Splash 在 `visual_ready` 后约 355 ms 隐藏。
- 完整启动约 706 ms。
- `desktop_state_load_bootstrap` round trip 约 242 ms。
- renderer invoke / deserialize overhead 约 31 ms。
- Rust 后端内存增量约 2.6 MB。
- renderer heap 启动增量约 7 MB。

这些数字不是硬性 SLA，但能作为后续回归判断的参考线。

## 诊断方式

安装版诊断日志位置：

```text
C:\Users\<user>\AppData\Local\OFPlayer\diagnostics\ofplayer-diagnostics.ndjson
```

重点看这些事件：

- `session_started`
- `tauri_setup`
- `bootstrap_state_loaded`
- `bootstrap_snapshot`
- `bootstrap_stores_hydrated`
- `bootstrap_active_track_hydrated`
- `bootstrap_state_ready`
- `startup_splash_hidden`
- `app_startup`

重点字段：

- `bootstrapRoundTripMs`
- `bootstrapBackendMs`
- `bootstrapInvokeOverheadMs`
- `catalogTracksIncluded`
- `catalogTrackCount`
- `trackCacheEntries`
- `rendererResources.delta.jsHeapUsedBytes`
- `bootstrapDiagnostics.process.delta.privateBytes`

SQLite 检查：

```sql
select key, length(value_json)
from app_state
order by length(value_json) desc
limit 10;

select count(*)
from app_state
where key = 'snapshot.catalog';
```

第二个查询应返回 `0`。

## 防回归规则

后续任何功能都不应把媒体数据重新塞回启动 payload。

尤其要避免：

- 在 bootstrap 中加入 `artwork`、歌词全文或 full track list。
- 在启动阶段遍历并解析每一行 `tracks.payload_json`。
- 在 renderer 首屏前 normalize 全量曲库。
- 把远程库同步、专辑浏览、艺术家浏览这类二级能力放回冷启动路径。
- 为了新窗口或 HUD 功能复制主播放器 store 的完整状态。

推荐做法：

- 启动只传 ID、计数、偏好、状态和小型摘要。
- 列表查询走投影。
- 完整曲目按需加载。
- 大视图在 visual ready 后懒加载。
- 新实验功能先挂显式开关，不自动影响主窗口启动。

## 对胶囊歌词的约束

Lyric Capsule 属于桌面级 HUD，但它不应该重新打开这条性能问题路径。

胶囊歌词只能接收胶囊级小 payload，例如：

- 当前曲目标题。
- 艺术家 / 专辑摘要。
- 播放 / 暂停状态。
- 当前歌词行。
- 进度锚点：`positionMs / durationMs / isPlaying / sentAtMs`。
- 小型音浪帧：8 个 `u8` level。

它不应直接订阅或复制完整曲库。若胶囊需要显示封面，只能同步当前曲目的胶囊级缩略图，
并且不能在进度或音浪刷新时重复传输同一份封面数据。主窗口只负责 open / close / toggle，
不再作为胶囊歌词、进度、封面或音浪的高频中转。

在窗口合成问题稳定前，胶囊窗口也应保持显式开关控制，避免影响主播放器玻璃背景。

## 相关文档

- [Startup Bootstrap Data Path Defect](startup-bootstrap-data-path.md)
