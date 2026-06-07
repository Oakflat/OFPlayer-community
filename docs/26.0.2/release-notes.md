# OFPlayer v26.0.2 Release Notes

日期：2026-05-20
类型：音频输出与 DSD 播放基础迭代

## 版本定位

`26.0.2` 是桌面端音频能力的小版本推进，重点建立 ASIO 输出设备枚举/选择基础，并让 DSD 文件进入本地导入、元数据读取和播放链路。

本版本只处理用户持有的本地音频文件能力，不加入 SACD ISO、DRM、版权绕过或受保护介质相关逻辑。

## 主要改进

### ASIO 输出基础

- 新增 `asio-output` Cargo feature，显式开启后会把 cpal 的 ASIO backend 纳入构建。
- Windows 输出设备现在带有 backend 标识，设备 ID 采用 `backend::device` 形式，便于区分 WASAPI 与 ASIO 设备。
- 保持旧设备名兼容，用户已有的纯设备名偏好会在匹配到实际设备后自动迁移为带 backend 的新 ID。
- 设置页和播放器输出菜单会对非 WASAPI 设备显示 backend 标签，避免多个 backend 下同名设备难以辨认。

### DSD 导入与播放

- 本地音频识别新增 `.dsf`、`.dff`，前端模型和 Rust 后端保持同一扩展名/MIME 边界。
- 元数据读取接入 DSD Reader，可提取基础采样率、时长、1-bit 位深等信息。
- 播放链路新增 DSD 到 PCM `f32` 的解码 Source，可通过现有 Rodio 输出管线播放 DSF/DFF。
- DSD 目前只支持从头播放；非零 seek 会被明确拒绝，避免给出不稳定的进度跳转体验。

## 明确不包含

- 不声明 native DSD、DoP、bit-perfect DSD 输出。
- 不声明 DST 压缩 DFF 的完整支持。
- 不支持 SACD ISO 或任何版权/DRM 绕过。
- ASIO 构建需要本机具备 bindgen 可用的 libclang 与 Windows SDK/MSVC 头文件环境；默认开发构建仍不强制开启 ASIO feature。

## 验证

- Rust 默认构建、测试和格式化。
- 前端单元测试、类型检查和生产构建。
- ASIO feature 编译检查。
- Git diff whitespace 检查。
