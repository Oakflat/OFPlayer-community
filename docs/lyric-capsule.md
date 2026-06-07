# Lyric Capsule 性能架构说明

Lyric Capsule 是 OFPlayer 的实验性桌面 HUD。当前目标是隔离播放关键路径：
主窗口只负责打开、关闭、切换胶囊窗口，不再作为歌词、进度、音浪或封面的高频中转站。

## 当前进度快照（2026-05-09 晚）

今日下午的实现已经把胶囊歌词从“主窗口 watcher 推 snapshot”的试验形态，推进到
“胶囊窗口主动 boot、Rust 直连小 payload、播放热路径隔离”的阶段 1 架构。

已落地：

- 胶囊窗口由主窗口显式打开 / 关闭 / 切换，主窗口不再高频转发歌词、进度、封面或音浪。
- 胶囊窗口 listener ready 后主动调用 `capsule_get_boot_state`，Rust `mark_ready()` 后才开始普通推送。
- Rust 增加 `capsule_state.rs`，负责 boot state、歌词行解析、progress anchor、backpressure 和 diagnostics ring。
- Rust 增加 `capsule_meter.rs`，由独立胶囊 meter emitter 读取 `AudioMeter` 的 atomic snapshot，向胶囊发送 8 段 `u8` 音浪帧。
- 播放 source 侧增加非阻塞 meter sample 路径和 FFT worker，播放解码不等待 UI、IPC、日志或 WebView。
- Rust 增加 `capsule_artwork_cache.rs`，把 data-url 封面缩成 app cache 下的 `128px` JPEG，胶囊 payload 只带短 `artworkKey / artworkSrc`。
- 前端胶囊窗口改为本地 `requestAnimationFrame` 插值进度，只接收 boot state、progress anchor 和 meter frame。
- `capsule_release` 会在窗口释放时写入 `capsule_diagnostics_summary`，用于回看慢发送、大 payload、慢 cache miss 等问题。
- `docs/startup-performance-optimization.md` 已补充胶囊歌词不能重新打开启动大 payload 路径的约束。

当前边界：

- 这仍是同进程、多线程的阶段 1 隔离，不是独立进程级 HUD。
- 胶囊只接收当前曲目的小状态，不订阅完整曲库，也不持有完整封面或歌词全文。
- 普通 state 校准周期为 `1.5s`；progress anchor 只在 play、pause、seek、track change、ended 等关键动作后发送。
- meter 帧允许丢弃，不补历史帧；backpressure 下会从正常频率降级到慢频率，严重时暂停普通更新。
- 外部非 data-url 封面最长只允许 `1024` 字符，避免异常 payload。

下一轮重点验证：

- 安装版打开 / 关闭胶囊时，确认首包不再出现接近 `2s` 的 `emitTo` 阻塞。
- 播放、暂停、拖动进度、切歌、播放结束时，确认胶囊进度锚点和本地插值一致。
- 大封面曲目切换时，确认胶囊 payload 没有重新携带 base64 封面，cache miss 慢路径会进入 diagnostics。
- 长时间播放时，确认 meter worker 不造成播放卡顿，backpressure 下 meter 可降频且不会影响主播放器。
- Windows 透明窗口边缘继续检查毛边、底板上下边距和不同 DPI 下的视觉稳定性。
- 如果后续要默认启用胶囊，需要先补安装版 smoke test 和回归指标记录。

## 窗口与职责

- 独立窗口 label：`lyric-capsule`。
- 页面入口：`/lyric-capsule.html`。
- 默认尺寸：`500 x 52`，可见底板高度为 `44px`。
- 主窗口文件：`src/App.vue` 只保留 `open / close / toggle`，不再 watch 播放状态后 `emitTo` 胶囊。
- 胶囊窗口文件：`src/components/LyricCapsuleWindow.vue` 自己注册监听器、主动拉 boot state、直接监听 Rust 事件。

```mermaid
flowchart LR
  Main["App.vue main window"] -->|open / close / toggle only| Window["lyric-capsule WebviewWindow"]
  Window -->|invoke capsule_get_boot_state after listeners ready| RustState["capsule_state.rs"]
  RustPlayback["playback.rs / PlaybackManager"] --> RustState
  RustState -->|emit_to capsule://state every 1.5s max| Window
  RustState -->|emit_to capsule://progress-anchor on play/pause/seek/track| Window
  RustMeter["capsule_meter.rs"] -->|emit_to capsule://meter small [u8; 8]| Window
  RustArtwork["capsule_artwork_cache.rs"] -->|artworkKey + local cache path| RustState
```

## 阶段 1 线程隔离

当前实现采用同进程、多工作线程的第一阶段隔离：

- Audio playback hot path：只解码和播放。`MeteredSource::next()` 只做多声道混 mono，并把 sample 非阻塞写入 meter ring buffer。
- Meter worker thread：独立线程从 ring buffer 读取 mono sample，执行 2048 点 FFT、8 段频谱映射和平滑，再写入 atomic `u8` levels。
- Capsule service threads：低频 state calibration 和关键 progress/state anchor 直接发给 `lyric-capsule`，不经过主窗口。
- Capsule meter thread：只读取 atomic meter 最新值并 `emit_to("lyric-capsule", "capsule://meter")`，不再锁 `PlaybackManager`。
- Artwork cache：data-url 转 128px JPEG cache 文件，payload 只带短 key/path。
- Diagnostics worker thread：Rust 侧 diagnostics command 只入队，worker 批量 flush NDJSON。

这意味着播放 source 不等待 UI、IPC、日志、封面处理、WebView 创建或胶囊事件发送。

## Ready 与首包

旧实现中，`isLyricCapsuleWindowActive` 可能在胶囊窗口 listener 完成前变成 true，主窗口 watcher 会提前发送首个 snapshot。
如果目标窗口未 ready，第一次 `emitTo` 可能阻塞接近 2 秒。

当前实现：

- `main_open_complete` 只等待窗口创建与配置，不等待首包。
- 胶囊窗口 mount 后先注册 `capsule://state`、`capsule://progress-anchor`、`capsule://meter`。
- listener ready 后，胶囊窗口主动 `invoke('capsule_get_boot_state')`。
- Rust command 内部 `mark_ready()`，之后 Rust 才允许普通 state / meter 推送。

## Payload

胶囊 boot state：

```ts
interface CapsuleBootState {
  seq: number
  trackId: string | null
  title: string
  artist: string
  lyricLine: string
  lyricVersion: number
  isPlaying: boolean
  durationMs: number
  positionMs: number
  sentAtMs: number
  artworkKey?: string
  artworkSrc?: string
}
```

进度锚点：

```ts
interface CapsuleProgressAnchor {
  seq: number
  trackId: string | null
  isPlaying: boolean
  durationMs: number
  positionMs: number
  sentAtMs: number
}
```

音浪帧：

```ts
interface CapsuleMeterFrame {
  seq: number
  trackId: string | null
  isPlaying: boolean
  levels: [number, number, number, number, number, number, number, number]
  sentAtMs: number
}
```

## 封面

- inline `data:image/...` 不再进入前端 `emitTo`、event 或 Channel payload。
- `capsule_artwork_cache.rs` 会把 data-url 解码为 `128px` JPEG 缩略图，写入 app cache 下的 `lyric-capsule-artwork`。
- 胶囊 payload 只携带 `artworkKey` 和短 `artworkSrc`。
- Tauri asset protocol 只开放 `$APPCACHE/lyric-capsule-artwork/**`，前端通过 `convertFileSrc()` 渲染缓存图。
- 外部非 data-url 封面最长限制为 `1024` 字符，避免异常 payload。

## 音浪

- 旧的全局 `playback://audio-levels` Rust 广播已停止。
- 主窗口默认也不会订阅 `playback://audio-levels`。
- 胶囊音浪由 `capsule_meter.rs` 直接读取 `PlaybackManager.audio_levels_snapshot()`。
- meter 只 `emit_to("lyric-capsule", "capsule://meter")`，payload 为 8 个 `u8` level。
- meter 帧可丢弃，不补历史帧。

## 进度

- 胶囊不再依赖主窗口每 200ms 推送进度。
- boot state 和 progress anchor 带 `positionMs / durationMs / isPlaying / sentAtMs`。
- 胶囊窗口用 `requestAnimationFrame` 本地插值播放进度。
- Rust 只在 play、pause、seek、reset、track change、ended 等关键动作后发 progress anchor。
- 普通播放中 Rust state 校准周期为 `1.5s`，且在 backpressure 下会停止普通校准。

## Backpressure 与 Diagnostics

Backpressure 由 Rust `CapsuleStateStore` 管理：

- send 耗时 `> 100ms`：进入 `2s` pressured，meter 降频到 `250ms`。
- send 耗时 `> 500ms`：进入 `5s` degraded，停止 meter 和普通 progress/state，只保留关键状态。
- send 耗时 `> 1500ms`：进入 paused，暂停普通 capsule 更新，等待 ready/reconnect。
- `capsule_release` 会把 ring buffer 汇总写入 diagnostics，事件名为 `capsule_diagnostics_summary`。

Diagnostics 降采样：

- 不记录每帧 meter。
- 不记录每次普通 progress。
- 只记录慢调用、大 payload、发送失败、慢 artwork cache miss。
- 阈值：`emitMs > 50ms`、`payloadBytes > 16KB`、`artwork cache miss > 100ms`。

## 视觉底板

胶囊底板不再使用布局级 border。当前使用：

- `border-radius: 999px`
- `clip-path: inset(0 round 22px)`
- 不透明底色
- 内层 pseudo-element 用 inset shadow 模拟极轻描边

这样可以减少透明窗口边缘和描边共同造成的视觉毛边，同时避免上下底板因为 border 参与布局而看起来不等边。

## 交互保护区

胶囊窗口保持完整渲染高度，避免 hover 控件被原生窗口裁切。Windows 上由 Rust 侧做鼠标坐标映射：默认让整窗透传，后台把全局 cursor position 映射到胶囊窗口坐标；只有光标落入顶部胶囊或展开控制区时，才临时关闭透传。它不裁剪窗口绘制本身，也不依赖 WebView2 子窗口 hit-test，因此能减少透明区域拦截和边缘露底色问题。非 Windows 平台会退回普通透明窗口行为。

## 相关文件

- `src/services/lyricCapsuleWindow.js`：创建、定位、配置、关闭 Tauri WebviewWindow。
- `src/services/lyricCapsuleWindowBounds.js`：胶囊窗口宽高和命中区收缩参数。
- `src/services/lyricCapsuleBridge.js`：胶囊窗口侧 bridge，负责 boot command 和 direct Rust event listener。
- `src/App.vue`：主窗口按钮编排，仅 open / close / toggle。
- `src/components/LyricCapsuleWindow.vue`：胶囊 UI、本地进度插值、meter 渲染。
- `src-tauri/src/capsule_window_region.rs`：Windows 鼠标坐标映射，动态切换胶囊窗口透传。
- `src/composables/useNativeAudioPlayer.js`：主窗口默认不再订阅音浪事件。
- `src-tauri/src/capsule_state.rs`：boot state、progress anchor、歌词行、backpressure、diagnostics ring。
- `src-tauri/src/capsule_artwork_cache.rs`：data-url 缩略图缓存和短封面引用。
- `src-tauri/src/capsule_meter.rs`：胶囊专用音浪 emitter。
- `src-tauri/src/lib.rs`：commands、setup、direct emit glue。
