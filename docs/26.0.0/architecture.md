# OFPlayer Tauri v26.0.0 系统架构

## 总体架构

OFPlayer Tauri 采用前后端分离的桌面应用架构：

```
┌─────────────────────────────────────────────────────┐
│                   Tauri 2 Shell                     │
│  ┌───────────────┐        ┌──────────────────────┐  │
│  │  Vue Frontend  │◄──────►│   Rust Backend       │  │
│  │  (WebView2)    │ invoke │   (Native)           │  │
│  │                │◄──────►│                      │  │
│  │  main.js       │ event  │  lib.rs              │  │
│  │  App.vue       │◄──────►│  desktop_state.rs    │  │
│  │  ofplayerApp.js│        │  playback.rs         │  │
│  │  stores/*      │        │  storage.rs          │  │
│  │  services/*    │        │  catalog_db.rs       │  │
│  └───────────────┘        └──────────────────────┘  │
│                                    │                │
│                          ┌─────────┴─────────┐      │
│                          │   SQLite (rusqlite) │      │
│                          └───────────────────┘      │
│                                                     │
│  ┌───────────────┐        ┌──────────────────────┐  │
│  │ Lyric Capsule  │◄──────►│  Capsule Services    │  │
│  │ (WebView2)     │ event  │  capsule_state.rs    │  │
│  │ capsule.html   │◄──────►│  capsule_meter.rs    │  │
│  │ LyricCapsule.. │        │  artwork_cache.rs    │  │
│  └───────────────┘        └──────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

## 前端架构

### 启动流程

```
index.html
  ├─ inline script: 解析 localStorage locale/colorScheme
  ├─ 设置 document.documentElement 属性
  └─ <script type="module" src="/src/main.js">
       │
       ├─ initializeWindowSurface()
       ├─ createApp(App)
       ├─ createOFPlayerApp()
       │   ├─ 创建 dataService (invoke 封装)
       │   ├─ 创建 fileImportService
       │   ├─ 创建 desktopStorageService
       │   ├─ 创建 libraryService / playlistService / trackService
       │   ├─ 创建 externalLibraryService
       │   ├─ 创建 lyricsService
       │   ├─ 创建 systemMediaService
       │   ├─ 创建 libraryStore / sessionStore / preferencesStore / playerStore
       │   └─ hydrateBootstrapState()
       │       ├─ invoke desktop_state_load_bootstrap
       │       ├─ libraryStore.hydrate()
       │       ├─ sessionStore.hydrate()
       │       ├─ preferencesStore.hydrate()
       │       ├─ playerStore.hydrate()
       │       └─ prepareBrowserCatalogForSelection()
       ├─ installOFPlayerApp(app, ofplayer)
       ├─ app.mount('#app')
       └─ hideStartupSplash()
```

### 核心编排：`ofplayerApp.js`

这是前端最核心的文件，职责：

1. **Service 创建**：实例化所有数据/存储/播放/歌词服务
2. **Store 初始化**：创建并连接 libraryStore、playerStore、sessionStore、preferencesStore
3. **Bootstrap Hydration**：冷启动时从 Rust 加载轻量状态并注入 stores
4. **导航管理**：监听 activeLibrary/activeCollection 变化，刷新 navigationSummary
5. **播放控制**：selectTrack、togglePlayback、playNext、playPrevious、seek
6. **导入流程**：文件选择 -> 扫描 -> 复制 -> 写入 catalog
7. **远程元数据**：外部曲库元数据延迟补齐队列
8. **存储监听**：autoScanOnLaunch + 文件系统 watcher
9. **生命周期**：dispose 清理所有 watcher、timer、listener

### 状态管理

采用 Vue 3 reactive refs，不使用 Pinia/Vuex：

| Store | 职责 |
|---|---|
| `libraryStore` | tracks、libraries、playlists、playlistTrackRelations、catalogRevision |
| `playerStore` | playback status、activeTrack、currentTime、duration、volume、history |
| `sessionStore` | currentTrackId、queue、currentTime、duration（持久化） |
| `preferencesStore` | language、theme、volume、storageRoot、scanDirectories、sortOption |

### 歌词胶囊窗口

独立入口 `lyricCapsuleMain.js`，独立 Vue 应用，独立 HTML：

```
lyric-capsule.html
  └─ lyricCapsuleMain.js
       ├─ createApp(LyricCapsuleWindow)
       └─ mount('#app')
```

胶囊窗口通过 `lyricCapsuleBridge.js` 与 Rust 通信：

- 主动调用 `capsule_get_boot_state` 获取初始状态
- 监听 `capsule://state`、`capsule://progress-anchor`、`capsule://meter` 事件
- 本地 `requestAnimationFrame` 插值播放进度

## 后端架构

### 模块职责

```
lib.rs (Tauri command 注册 + glue)
  │
  ├── desktop_state.rs ─── catalog_db.rs (SQLite CRUD)
  │                   ├── navigation.rs (导航摘要)
  │                   ├── sorting.rs (排序过滤)
  │                   └── schema.rs (表结构)
  │
  ├── storage.rs ──────── 托管存储、文件复制、安全校验
  ├── storage_maintenance.rs ── 占用分析、GC
  ├── watcher.rs ────────── 目录监听
  │
  ├── playback.rs ───────── rodio 播放、输出设备、meter
  ├── lyrics.rs ─────────── 歌词文件解析
  ├── metadata.rs ───────── lofty 元数据解析
  │
  ├── capsule_state.rs ──── 胶囊 boot state、歌词行、backpressure
  ├── capsule_meter.rs ──── 胶囊 8 段音浪
  ├── capsule_artwork_cache.rs ── 胶囊封面缓存
  ├── capsule_window_region.rs ── Windows 命中区
  │
  ├── external_sources.rs ── WebDAV/Navidrome
  ├── system_media.rs ────── Windows SMTC
  ├── diagnostics.rs ─────── NDJSON 日志
  └── app_paths.rs ──────── 路径解析
```

### SQLite Schema

核心表：

| 表 | 内容 |
|---|---|
| `libraries` | 曲库定义（id, name, order, source） |
| `playlists` | 歌单定义 |
| `tracks` | 曲目索引和轻量元数据；`payload_json` 不允许存封面 base64 |
| `track_artwork` | 曲目封面指针、mime、content hash 和逻辑尺寸 |
| `playlist_track_relations` | 歌单-曲目多对多关系 |
| `playback_history` | 播放历史记录 |
| `app_state` | 键值对存储（preferences, session, bootstrap 等） |

### 线程模型

```
Main Thread (Tauri)
  │
  ├── Playback Thread ─── rodio 解码 + 播放
  │     └── Meter Worker ── FFT 音浪采样
  │
  ├── Capsule State Thread ── 1.5s 校准周期
  ├── Capsule Meter Thread ── 读取 atomic levels
  ├── Capsule Artwork Thread ── 封面缩略图
  │
  ├── Storage Watcher Thread ── notify 文件监听
  ├── Diagnostics Worker ── 批量 flush NDJSON
  └── Import Job Threads ── 扫描/复制/解析
```

### 播放热路径隔离

```
MeteredSource::next() [播放线程]
  │
  ├─ 解码音频样本
  ├─ 混合为 mono
  └─ 非阻塞写入 ring buffer
       │
       └─ Meter Worker [独立线程]
            ├─ 从 ring buffer 读取
            ├─ 2048 点 FFT
            ├─ 8 段频谱映射 + 平滑
            └─ 写入 atomic u8 levels
                 │
                 └─ Capsule Meter Thread [独立线程]
                      └─ emit_to("lyric-capsule", "capsule://meter")
```

## 数据流

### 冷启动

```
Rust setup
  → 创建 runtime dirs
  → 初始化 DesktopStateStore + PlaybackManager
  → 初始化 SQLite schema

Vue bootstrap
  → invoke desktop_state_load_bootstrap (轻量 shell)
  → hydrate stores (preferences, session, catalog shell, history)
  → 首屏 ready
  → 延后：播放同步、远程元数据、目录监听、自动扫描
```

### 导入

```
用户选择文件/目录
  → invoke desktop_library_scan_import
  → Rust: 扫描目录、过滤已导入
  → storage.rs: 复制到托管目录、解析元数据
  → desktop_state.rs: 写入 tracks + relations
  → 前端同步: catalogRevision++, 刷新导航
```

### 播放

```
用户点击曲目
  → invoke playback_session_select_track
  → Rust: 更新 session、加载音频文件到 PlaybackManager
  → playback.play()
  → 启动 snapshot emitter (250ms)
  → emit playback://snapshot 到前端
  → 胶囊: emit capsule://state / progress-anchor / meter
```

## 安全边界

### 托管存储

- `<storageRoot>/OFPlayer Library/` 必须带 `.ofplayer-managed-storage.json` marker
- 清理前校验 marker，拒绝 symlink
- GC 只清 orphan 文件和可再生缓存

### Tauri 权限

- 主窗口 `default.json`：~100 条 permission
- 胶囊窗口 `lyric-capsule.json`：仅 capsule + window 权限
- 权限由 `build.rs` 自动生成 TOML

### Bootstrap 防膨胀

启动只传：ID、计数、偏好、状态、摘要
不传：完整 tracks、artwork base64、歌词全文、远程库列表

### 曲库封面存储

- `tracks.payload_json` 是热路径索引，不存封面 base64。
- 封面写入 `track_artwork` 和本地 `track-artwork/` 资产目录。
- 前端只在当前曲目或需要封面的视图按需取封面，并通过 Tauri asset URL 渲染。
- WebDAV 同步只索引远端文件；封面在播放或显式补齐时从本地播放缓存解析并托管。
