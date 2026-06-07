# OFPlayer Tauri v26.0.0 文档总览

版本：`26.0.0`
日期：2026-05-12
基线提交：`9c3b8dd fix(app): stabilize capsule window creation`

## 版本定位

`26.0.0` 是 OFPlayer 桌面分支的第一条可公开发布基线。它代表从 Web PWA 形态到 Tauri 2 桌面应用的完整迁移，覆盖本地音频管理、SQLite 持久化、Rust 播放引擎、歌词胶囊 HUD、外部曲库接入和系统媒体控制。

## 文档索引

| 文档 | 内容 |
|---|---|
| [project-structure.md](project-structure.md) | 完整项目目录结构、前端/后端/脚本/配置文件逐一说明 |
| [architecture.md](architecture.md) | 系统架构、数据流、线程模型、前后端职责边界 |
| [git-timeline.md](git-timeline.md) | 从 init 到 HEAD 的完整 git 提交时间线、关键里程碑和变更分类 |
| [usage-guide.md](usage-guide.md) | 用户视角的功能用法：导入、播放、歌词、设置、存储管理 |
| [development-guide.md](development-guide.md) | 开发者指南：环境搭建、命令、新增 command checklist、调试、测试 |

## 核心能力一览

- **本地音频导入与托管存储**：扫描目录 -> 复制到 managed storage -> SQLite catalog 持久化
- **Rust 播放引擎**：基于 rodio 的音频播放、输出设备选择、音量控制、FFT 音浪采样
- **歌词系统**：本地歌词文件匹配、时间戳解析、歌词胶囊独立 HUD 窗口
- **外部曲库**：WebDAV / Navidrome 远程接入、播放缓存、元数据补齐
- **系统媒体控制**：Windows SMTC 集成、锁屏/任务栏媒体控制
- **沉浸式播放器**：WebGL 背景渲染、Oklab 色彩插值、dithering 去色阶
- **存储管理**：占用分析、orphan GC、SQLite vacuum、cache 清理
- **诊断系统**：NDJSON 本地日志、启动性能指标、资源采样

## 技术栈

| 层 | 技术 |
|---|---|
| 前端框架 | Vue 3.5 + Composition API |
| 构建工具 | Vite 8 + esbuild |
| 桌面壳 | Tauri 2 |
| 后端语言 | Rust 2021 edition |
| 数据库 | SQLite (rusqlite, bundled) |
| 音频播放 | rodio 0.21 |
| 元数据解析 | lofty 0.24 |
| 系统媒体 | souvlaki 0.8 (Windows SMTC) |
| HTTP | reqwest 0.13 (rustls) |
| 文件监听 | notify 8.2 |
| 图标 | lucide-vue-next |
