# OFPlayer Tauri Community

> 音乐能跨越我们与彼此之间的偏见，跨越时间与空间，传达作者的想法。

OFPlayer Tauri Community 是 OFPlayer 的桌面社区版本。它保留 Vue + Vite 前端体验，并通过 Rust/Tauri 提供本地文件访问、SQLite 持久化、桌面播放、歌词胶囊、系统媒体集成和本地诊断能力。

当前版本为 `26.0.4`，包含新一轮视觉调整和完全在本地生成的聆听统计功能。

这个仓库面向公开社区维护，移除了私有账号层级、官方更新器、商店打包流程和远程诊断上传，保留可本地构建、可审阅、可二次开发的桌面播放器基线。

## 致谢

首先，我想感谢所有默默关注以及提供正反馈的人。正是因为大家的支持，让我有着极大的动力推进这个项目。

我一直认为好的音乐能够极大改变一个人，那么好的载体也是。这也是我制作 OFPlayer 的初衷——我希望音乐能跨越我们与彼此之间的偏见，跨越时间与空间，传达作者的想法。

OFPlayer 也希望传达我的想法：以漂亮的 UI、深思熟虑的功能为社区带来动力。因此，我们的大部分代码将长期保持 MIT 许可证。

## 项目定位

OFPlayer Tauri Community 适合：

- 桌面端 local-first 音乐播放器开发
- Rust/Tauri 音频、存储和系统集成参考
- WebDAV / Navidrome 等自托管音乐库接入
- 歌词、沉浸播放和桌面窗口体验探索
- 社区版本维护和二次开发

当前版本不包含：

- 登录、账号、云同步
- 官方自动更新服务
- 商店专用打包脚本
- 远程诊断上传
- 私有发布流程

## 功能特性

| 能力 | 说明 |
| --- | --- |
| 本地导入 | 导入本地音频并解析元数据 |
| SQLite catalog | 使用 SQLite 保存曲库、播放列表、会话和偏好 |
| 播放队列 | 支持队列、上一首、下一首和会话恢复 |
| 聆听统计 | 基于本地播放历史展示时长、热力图、单曲排行和专辑分布 |
| 歌词系统 | 支持歌词显示、解析和歌词胶囊窗口 |
| 沉浸播放 | 使用 WebGL 背景渲染沉浸式播放器 |
| 外部曲库 | 支持 WebDAV / Navidrome 连接和同步 |
| 系统媒体 | 集成 Windows SMTC 系统媒体控制 |
| 本地诊断 | 保留本地诊断、性能记录和存储维护工具 |

## 技术栈

| 技术 | 用途 |
| --- | --- |
| Vue 3 | 前端框架 |
| Vite | 前端开发服务器和构建工具 |
| TypeScript | 前端类型系统 |
| Rust + Tauri 2 | 桌面原生能力 |
| SQLite / rusqlite | 本地 catalog 持久化 |
| rodio | 音频播放引擎 |
| lofty | 音频元数据解析 |
| souvlaki | Windows 系统媒体控制 |
| notify | 文件监听 |

## 快速开始

### 环境要求

- Node.js 18+
- npm
- Rust
- Tauri 2 所需系统依赖

Tauri 环境准备可参考官方 prerequisites 文档：<https://tauri.app/start/prerequisites/>

### 安装与运行

```bash
npm install
npm run tauri:dev
```

### 常用命令

| 命令 | 用途 |
| --- | --- |
| `npm run dev` | 启动前端开发服务器 |
| `npm run build` | 类型检查并构建前端 |
| `npm run test:unit` | 运行前端单元测试 |
| `npm run check:tauri-commands` | 校验 Tauri command 和权限清单 |
| `npm run tauri:dev` | 启动 Tauri 开发模式 |
| `npm run tauri:build` | 构建桌面应用 |
| `npm run tauri:build:all` | 构建 NSIS/MSI 安装包 |

### Rust 后端检查

```bash
cd src-tauri
cargo fmt
cargo check
cargo test
cargo clippy --all-targets -- -D warnings
```

## 代码结构

```text
ofplayer-tauri/
├─ src/                    # Vue 前端
│  ├─ app/                 # 应用编排层
│  ├─ components/          # Vue 组件
│  ├─ services/            # 领域服务层
│  ├─ stores/              # 状态管理
│  ├─ composables/         # 组合式函数
│  ├─ models/              # 数据模型
│  ├─ styles/              # 样式系统
│  └─ themes/              # 主题配置
├─ src-tauri/              # Rust 后端
│  ├─ src/                 # Rust 源码
│  ├─ capabilities/        # Tauri 权限配置
│  ├─ permissions/         # 自动生成的权限清单
│  └─ Cargo.toml           # Rust 依赖配置
├─ docs/                   # 技术文档
├─ scripts/                # 构建与校验脚本
├─ public/                 # 静态资源
├─ vite.config.ts          # Vite 配置
└─ package.json
```

## 架构概览

### 前端模块

| 模块 | 职责 |
| --- | --- |
| `src/app` | 应用编排、Service/Store 装配、生命周期管理 |
| `src/components` | 播放器、曲库、设置、歌词、窗口等 Vue 组件 |
| `src/services` | 数据、播放、歌词、外部库、诊断等领域服务 |
| `src/stores` | 曲库、播放器、会话、偏好等状态管理 |
| `src/composables` | 音频播放、歌词、国际化等组合式函数 |

### Rust 后端模块

| 模块 | 职责 |
| --- | --- |
| `lib.rs` | Command 注册、播放控制 glue、应用 setup |
| `desktop_state.rs` | Catalog / Preferences / Session 状态管理 |
| `catalog_db.rs` | SQLite CRUD 操作 |
| `playback.rs` | rodio 播放引擎、输出设备、音量控制 |
| `storage.rs` | 托管存储、安全校验、扫描、导入 |
| `lyrics.rs` | 歌词文件解析、目录搜索和匹配 |
| `external_sources.rs` | WebDAV / Navidrome 外部曲库接入 |
| `system_media.rs` | Windows SMTC 系统媒体控制 |

## 文档

- [docs](docs/)：桌面端技术文档
- [docs/startup-performance-optimization.md](docs/startup-performance-optimization.md)：启动性能优化记录
- [docs/lyric-capsule.md](docs/lyric-capsule.md)：歌词胶囊说明
- [docs/asio-dsd-playback.md](docs/asio-dsd-playback.md)：ASIO 与 DSD 播放边界

## 参与开发

提交前建议运行：

```bash
npm run build
npm run test:unit
npm run check:tauri-commands
cd src-tauri
cargo check
```

## 许可证

本仓库采用双许可证结构：

- 前端、文档和静态资源采用 MIT 许可证，详见 [LICENSE-MIT](LICENSE-MIT)。
- Rust/Tauri 后端采用 Apache License 2.0，详见 [src-tauri/LICENSE](src-tauri/LICENSE)。
- 许可证范围说明见 [LICENSE](LICENSE)。

> 好的音乐值得好的载体。让我们一起构建更好的音乐体验。
