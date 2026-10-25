# 开发说明

## 目录

| 路径 | 职责 |
| --- | --- |
| crates/community-core | 本地音频、曲库、歌词、导入与旧数据迁移 |
| crates/community-host | 音乐播放会话和本地进程通信 |
| src-winui/OFPlayer.Community | WinUI 界面和系统媒体控制 |
| src-winui/OFPlayer.WindowsMedia | Windows 本地视频后端 |
| src-winui/OFPlayer.Community.Tests | 使用模拟 HTTP 和临时文件的托管测试 |
| tests | 原生进程契约与依赖边界测试 |

音乐播放状态由原生 host 统一维护。视频在 WinUI 进程内使用 MediaPlayerElement，切换到视频时释放音乐输出，返回音乐时停止视频。应用退出后回收自己的原生子进程。

## 构建

先按 README 构建 Rust host，再构建 WinUI。依赖从 crates.io 和 NuGet 的公共源恢复，锁文件随源码保存。视频模块中的 `Native/compile.ps1` 仅负责从 C++ 源码编译本地 DLL。

构建结果用于本机开发与测试。仓库不提供签名、商店提交、分发打包或发布脚本。

## 测试

在仓库根目录执行：

```powershell
$env:CARGO_TARGET_DIR = Join-Path $PWD '.artifacts/native'
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
python tests/community_boundary.py
```

Rust 存储测试应使用临时数据目录，避免触及本机曲库：

```powershell
$previousLocalData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $env:TEMP ('ofplayer-community-tests-' + [guid]::NewGuid())
    cargo test --workspace --locked
} finally {
    $env:LOCALAPPDATA = $previousLocalData
}
```

完成 Release 构建后运行：

```powershell
python tests/host_contract.py --host .artifacts/native/release/ofplayer-community-host.exe
dotnet run --project src-winui/OFPlayer.Community.Tests -c Release -p:RestoreLockedMode=true
```

测试使用合成媒体、临时数据库和模拟 HTTP，不调用真实账号或统计接口。覆盖曲库操作、失败切歌、后台导入、远程命令拒绝、迁移和每日活跃统计。

## 迁移

首次启动且目标目录不存在时，从旧数据库建立包含已提交 WAL 的一致快照。快照清理远程曲目、非本地状态和凭据，重建数据库以去除空闲页残留，再复制并重定位本地封面。完成后原子更名为目标目录。

源库和媒体原文件保留，已有目标目录不覆盖。迁移失败保留暂存数据并显示错误；从曲库移除歌曲只改数据库关系。

## 播放验收

单元测试不能代替声卡、不同编码、字幕、4K/HDR、超大型曲库及真实迁移验收。系统视频编码支持依赖 Windows 环境；默认构建不包含可选 FFmpeg 视频解码或 ASIO 组件。
