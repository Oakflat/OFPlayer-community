# OFPlayer Community

OFPlayer Community 是专注本机音乐和视频播放的 Windows 社区版，接替原 Tauri Community。

- 音乐：本地导入、曲库、歌单、收藏、播放队列、歌词和输出设备选择。
- 视频：本地文件、字幕、倍速、全屏和播放进度恢复。
- 不包含一起听、远程串流、设备互联、云曲库、账号及购买功能。

界面使用 WinUI，音乐内核使用 Rust，视频使用 Windows Media。本仓包含独立构建所需的源码和公共依赖，不包含产品分发流程。

## 本地构建

需要 Windows、PowerShell 7、Rustup、.NET SDK，以及带 C++ x64 工具和 Windows SDK 的 Visual Studio Build Tools。具体工具链版本由 `rust-toolchain.toml`、`global.json` 和项目文件固定。

在仓库根目录执行：

```powershell
$env:CARGO_TARGET_DIR = Join-Path $PWD '.artifacts/native'
cargo build --workspace --release --locked
dotnet build src-winui/OFPlayer.Community -c Release -p:Platform=x64 -p:RestoreLockedMode=true
```

本机运行入口为 `.artifacts/dotnet/OFPlayer.Community/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/OFPlayer.Community.exe`。

构建和测试说明见 [开发文档](docs/development.md)。

## 数据与隐私

数据保存到 `%LOCALAPPDATA%/ofplayer-community`。首次启动时，若新目录不存在，可以从旧 `%LOCALAPPDATA%/ofplayer` 迁入本地曲库、歌单、收藏、历史、位置及封面。原数据库和媒体文件保留，已有 Community 数据不会被覆盖，账号、许可证、配对和远程连接凭据不会迁入。迁移前请退出旧 Tauri 客户端。

Community 包含必需的最小每日活跃统计，没有诊断页开关。统计不包含曲名、路径、媒体内容、账号或硬件标识，网络不可用不影响本地播放。详见 [隐私说明](docs/privacy.md)。

## 许可

WinUI、测试、文档和构建配置采用 MIT；Rust 内核与 host 采用 Apache-2.0。第三方源码保留各自许可。详见 [LICENSE](LICENSE)。
