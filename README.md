# OFPlayer Community

> 音乐能跨越我们与彼此之间的偏见，跨越时间与空间，传达作者的想法。

**OFPlayer Community** 是专注本机音乐与视频播放的 Windows 桌面社区版，正式接替原 Tauri Community。

这一代客户端告别了 WebView 方案，采用 **WinUI 原生界面 + Rust 音乐内核 + Windows Media 视频引擎** 的全新架构，专注于纯粹、流畅的 Local-First 桌面视听体验。本仓库面向公开社区维护，包含独立构建所需的完整源码与公共依赖，保留可本地构建、可审阅、可二次开发的桌面播放器基线。

---

## 致谢与初心

首先，感谢所有默默关注以及提供正反馈的朋友。正是因为大家的支持，才让我有持续推进这个项目的动力。

我一直认为，好的音乐能够极大地改变一个人，而好的载体同样重要。这也是制作 OFPlayer 的初衷——我希望音乐能跨越偏见、时间与空间，去传达创作者的想法；同时，我也希望通过 OFPlayer 传达自己的理念：**以干净漂亮的 UI 和深思熟虑的功能，为社区带来纯粹的体验。** 因此，我们的大部分代码将长期保持开源许可。

---

## 功能与边界

OFPlayer Community 专注于打磨本机媒体播放体验，去除了复杂的云端与商业化包袱：

| 模块 | 核心支持能力 |
| :--- | :--- |
| **本机音乐** | 本地音频导入、曲库管理、歌单、收藏、播放队列、本地歌词显示、音频输出设备选择 |
| **本机视频** | 本地视频文件播放、外挂/内嵌字幕加载、倍速调节、全屏沉浸播放、播放进度自动恢复 |
| **底层架构** | **WinUI** 原生桌面交互界面 + **Rust** 高性能音频内核 + **Windows Media** 视频渲染 |

**社区版不包含的内容：**

- 不包含一起听、远程串流、设备互联及云曲库功能
- 不包含账号系统、登录同步及许可证购买流程
- 不包含官方自动更新服务与商店专用打包分发流程

---

## 快速开始（本地构建）

### 环境要求

构建本项目需要 **Windows** 环境，并提前配置好以下工具链（具体版本已由仓库根目录的 `rust-toolchain.toml`、`global.json` 及项目工程文件固定）：

- **PowerShell 7**
- **Rustup**（Rust 工具链）
- **.NET SDK**
- **Visual Studio Build Tools**（需勾选 C++ x64 编译工具与 Windows SDK）

### 编译与运行

在仓库根目录下打开 PowerShell 7，依次执行以下命令完成 Rust 原生内核与 WinUI 客户端的构建：

```powershell
# 1. 指定 Rust 产物输出目录并构建原生音乐内核
$env:CARGO_TARGET_DIR = Join-Path $PWD '.artifacts/native'
cargo build --workspace --release --locked

# 2. 构建 WinUI 桌面客户端
dotnet build src-winui/OFPlayer.Community -c Release -p:Platform=x64 -p:RestoreLockedMode=true
```

本机运行入口为 `.artifacts/dotnet/OFPlayer.Community/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/OFPlayer.Community.exe`。

更多构建与测试说明见 [开发文档](docs/development.md)。本仓不包含内部签名、商店提交或分发打包脚本。

---

## 数据与隐私

数据保存到 `%LOCALAPPDATA%/ofplayer-community`。首次启动时，若新目录不存在，可以从旧 `%LOCALAPPDATA%/ofplayer` 迁入本地曲库、歌单、收藏、历史、位置及封面。原数据库和媒体文件保留，已有 Community 数据不会被覆盖，账号、许可证、配对和远程连接凭据不会迁入。迁移前请退出旧 Tauri 客户端。

Community 包含必需的最小每日活跃统计，没有诊断页开关。统计不包含曲名、路径、媒体内容、账号或硬件标识，网络不可用不影响本地播放。详见 [隐私说明](docs/privacy.md)。

## 许可

WinUI、测试、文档和构建配置采用 MIT；Rust 内核与 host 采用 Apache-2.0。第三方源码保留各自许可。详见 [LICENSE](LICENSE)。
