# OFPlayer Tauri v26.0.0 项目结构

## 顶层目录

```
ofplayer-tauri/
├── .git/                       # Git 仓库
├── .gitignore
├── .vscode/                    # VSCode 配置
├── dist/                       # Vite 构建产物（git ignored）
├── docs/                       # 项目文档
│   ├── 26.0.0/                 # 本版本文档
│   ├── immersive-background-banding.md
│   ├── lyric-capsule.md
│   ├── recent-change-handoff-2026-05-12.md
│   ├── startup-bootstrap-data-path.md
│   └── startup-performance-optimization.md
├── lyric-capsule-keynote/      # 歌词胶囊演示素材（未跟踪）
├── node_modules/               # npm 依赖
├── public/                     # 静态资源
│   ├── OFplayer.svg            # 应用图标 SVG 源
│   └── privacy.md              # 隐私政策
├── scripts/                    # 构建脚本
│   └── run-tauri.js            # Tauri CLI 启动包装器
├── src/                        # Vue 前端源码
├── src-tauri/                  # Tauri/Rust 后端
├── index.html                  # 主窗口 HTML 入口
├── lyric-capsule.html          # 歌词胶囊窗口 HTML 入口
├── package.json
├── package-lock.json
├── vite.config.js              # Vite 配置
├── tauri-dev.err.log           # 开发日志
├── tauri-dev.out.log
└── README.md
```

## 前端 `src/` 详细结构

```
src/
├── App.vue                     # 主窗口根组件
├── main.js                     # 主窗口入口（bootstrap + splash）
├── lyricCapsuleMain.js         # 歌词胶囊窗口入口
├── style.css                   # 全局样式
│
├── app/
│   └── ofplayerApp.js          # 应用编排核心（~2000行）
│                               # 创建所有 service/store，管理生命周期
│                               # bootstrap hydration、导航、扫描、远程元数据
│
├── components/                 # Vue 组件
│   ├── AlbumBrowserPanel.vue   # 专辑浏览器
│   ├── DialogModal.vue         # 通用对话框
│   ├── ExternalLibraryDialog.vue # 外部曲库连接对话框
│   ├── ImmersivePlayerView.vue # 沉浸式播放器（WebGL 背景）
│   ├── LibraryPanel.vue        # 左侧曲库导航面板
│   ├── LyricCapsuleWindow.vue  # 歌词胶囊 HUD 组件
│   ├── LyricsPanel.vue         # 歌词面板
│   ├── LyricsPlayerView.vue    # 歌词播放视图
│   ├── MenuDropdown.vue        # 下拉菜单
│   ├── OnboardingGuide.vue     # 首次使用引导
│   ├── PlayerPanel.vue         # 主播放面板
│   ├── SettingsModal.vue       # 设置中心
│   ├── TelemetryConsentDialog.vue # 隐私诊断同意
│   ├── TrackPlaylistDialog.vue # 曲目歌单操作
│   └── WindowTitlebar.vue      # 自定义标题栏
│
├── composables/                # Vue 组合式函数
│   ├── useAudioPlayer.js       # 音频播放器 composable
│   ├── useI18n.js              # 国际化 composable
│   ├── useLyrics.js            # 歌词解析 composable
│   └── useNativeAudioPlayer.js # 原生音频播放 composable
│
├── models/                     # 数据模型
│   ├── collection.js           # 集合模型
│   ├── externalLibrary.js      # 外部曲库模型
│   ├── library.js              # 曲库模型
│   ├── libraryNavigation.js    # 导航视图模型
│   ├── lyrics.js               # 歌词模型
│   ├── playback.js             # 播放状态模型
│   ├── playbackHistory.js      # 播放历史模型
│   ├── playlist.js             # 歌单模型
│   ├── playlistTrackRelation.js # 歌单-曲目关系
│   ├── preferences.js          # 偏好设置模型
│   ├── session.js              # 会话模型
│   └── track.js                # 曲目模型
│
├── services/                   # 服务层
│   ├── data/                   # 数据服务
│   │   ├── index.js            # 服务入口
│   │   └── desktopDataService.js # Tauri invoke 封装
│   ├── albumViewService.js     # 专辑视图服务
│   ├── catalogHelpers.js       # catalog 辅助函数
│   ├── catalogState.js         # catalog 状态管理
│   ├── desktopStorageService.js # 桌面存储服务（invoke 封装）
│   ├── diagnosticsLogger.js    # 诊断日志服务
│   ├── diagnosticsProfiler.js  # 诊断性能分析
│   ├── externalLibraryService.js # 外部曲库服务
│   ├── fileImportService.js    # 文件导入服务
│   ├── libraryService.js       # 曲库服务
│   ├── lyricCapsuleBridge.js   # 歌词胶囊通信桥
│   ├── lyricCapsuleDiagnostics.js # 歌词胶囊诊断
│   ├── lyricCapsuleWindow.js   # 歌词胶囊窗口管理
│   ├── lyricCapsuleWindowBounds.js # 胶囊窗口尺寸/命中区
│   ├── lyricsService.js        # 歌词服务
│   ├── metadataService.js      # 元数据服务
│   ├── navigationQueryService.js # 导航查询服务
│   ├── playlistService.js      # 歌单服务
│   ├── sortingService.js       # 排序服务
│   ├── systemMediaService.js   # 系统媒体控制服务
│   ├── telemetryService.js     # 遥测服务
│   ├── trackService.js         # 曲目服务
│   └── windowSurface.js        # 窗口表面初始化
│
├── stores/                     # 状态管理
│   ├── libraryStore.js         # 曲库状态（tracks, libraries, playlists）
│   ├── playerStore.js          # 播放器状态（playback, history, activeTrack）
│   ├── preferencesStore.js     # 偏好设置状态
│   ├── sessionStore.js         # 会话状态（currentTrackId, queue, position）
│   └── uiStore.js              # UI 状态
│
├── styles/
│   └── tokens/                 # 设计 token
│       ├── of-color-tokens.css # 颜色 token
│       ├── of-component-tokens.css # 组件 token
│       ├── of-player-tokens.css # 播放器 token
│       └── of-theme-mapping.css # 主题映射
│
├── themes/                     # 主题系统
│   ├── index.js                # 主题入口
│   ├── material.css / .js      # Material 主题
│   ├── mist.css / .js          # Mist 主题
│   └── paper.css / .js         # Paper 主题
│
└── utils/                      # 工具函数
    ├── colorExtractor.js       # 封面色彩提取（384x384 采样）
    └── immersiveBackgroundRenderer.js # WebGL 沉浸背景渲染
```

## 后端 `src-tauri/` 详细结构

```
src-tauri/
├── Cargo.toml                  # Rust 依赖配置
├── Cargo.lock
├── build.rs                    # Tauri 构建脚本（command 权限生成）
├── tauri.conf.json             # Tauri 应用配置
├── src-tauri.lnk               # Windows 快捷方式
│
├── capabilities/               # Tauri 权限能力
│   ├── default.json            # 主窗口权限（~100 条 permission）
│   └── lyric-capsule.json      # 歌词胶囊窗口权限
│
├── gen/                        # Tauri 自动生成代码
├── icons/                      # 应用图标
│   ├── 32x32.png
│   ├── 128x128.png
│   ├── 128x128@2x.png
│   ├── icon.icns
│   └── icon.ico
│
├── installer/                  # 安装器资源
│   ├── generate-installer-assets.ps1 # 安装器资源生成脚本
│   ├── logo.svg                # SVG logo 源
│   ├── nsis-header.bmp         # NSIS 安装器头部图
│   ├── nsis-sidebar.bmp        # NSIS 侧边栏图
│   ├── nsis-hooks.nsh          # NSIS 自定义钩子
│   ├── nsis/                   # NSIS 语言文件
│   │   ├── English.nsh
│   │   └── SimpChinese.nsh
│   ├── wix-banner.bmp          # WiX 安装器横幅
│   ├── wix-dialog.bmp          # WiX 对话框图
│   └── wix/                    # WiX 语言文件
│       ├── en-US.wxl
│       └── zh-CN.wxl
│
├── permissions/                # Tauri 自动生成的权限 TOML
│   └── autogenerated/
│
├── src/                        # Rust 源码
│   ├── main.rs                 # 入口（调用 lib::run()）
│   ├── lib.rs                  # Tauri 应用注册、command glue（~2800行）
│   │
│   ├── app_paths.rs            # AppData / cache / diagnostics 路径解析
│   ├── schema.rs               # SQLite schema 初始化与迁移
│   ├── db_helpers.rs           # SQLite JSON、字段读写、时间工具
│   ├── desktop_state.rs        # catalog / preferences / session / bootstrap 门面
│   ├── desktop_types.rs        # 前后端共享的请求/响应类型定义
│   ├── catalog_db.rs           # catalog 表读写、曲库/歌单/曲目事务
│   ├── navigation.rs           # 导航摘要、列表查询、投影查询
│   ├── sorting.rs              # 曲目过滤排序结果模型
│   │
│   ├── storage.rs              # 托管存储、安全校验、扫描、导入准备
│   ├── storage_maintenance.rs  # 存储占用分析和 GC
│   ├── watcher.rs              # 扫描目录文件监听
│   │
│   ├── playback.rs             # 播放引擎、输出设备、音量、meter
│   ├── lyrics.rs               # 歌词解析、目录搜索和匹配
│   ├── metadata.rs             # 音频元数据解析（lofty）
│   │
│   ├── capsule_state.rs        # 歌词胶囊 boot state、歌词行、backpressure
│   ├── capsule_meter.rs        # 歌词胶囊 8 段音浪 emitter
│   ├── capsule_artwork_cache.rs # 歌词胶囊封面缩略图缓存
│   ├── capsule_window_region.rs # Windows 胶囊窗口命中区/透传
│   │
│   ├── external_sources.rs     # WebDAV / Navidrome 外部曲库
│   ├── system_media.rs         # Windows 系统媒体控制（SMTC）
│   ├── diagnostics.rs          # NDJSON 诊断日志、资源采样
│   └── session_ops.rs          # 会话操作辅助
│
└── target/                     # Rust 编译产物（git ignored）
```

## 配置文件说明

### `package.json`

- 版本号：`26.0.0`
- 关键依赖：`@tauri-apps/api ^2.10.1`、`vue ^3.5.32`
- 脚本：`dev`、`build`、`tauri`、`tauri:dev`、`tauri:build`

### `vite.config.js`

- 双入口：`index.html`（主窗口）+ `lyric-capsule.html`（胶囊窗口）
- 开发端口：`5173`
- 构建目标：Windows `chrome105`，其他 `safari13`
- CSS 不拆分、不压缩（WebView2 backdrop-filter 兼容）

### `tauri.conf.json`

- 标识符：`org.ofplayer.community`
- 主窗口：`1440x900`，最小 `1160x720`，无边框，透明
- 安装器：NSIS + WiX，中英双语
- Asset protocol 启用

### `Cargo.toml`

- Rust edition 2021
- 关键依赖：rodio（播放）、lofty（元数据）、rusqlite（数据库）、souvlaki（系统媒体）、reqwest（HTTP）、notify（文件监听）、rustfft（FFT 音浪）
