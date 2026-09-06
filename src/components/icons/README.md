# 音乐图标

运行时通过 `MusicIcon.ts` 渲染内联 SVG，颜色继承 `currentColor`。

- `userMusicIconPaths.ts`：用户提供的 12 组导航和操作图标轮廓，保留原始路径与坐标变换。
- `musicIconPaths.ts`：图标类型、路径组合和 8 个曲目导航音符。
- `MusicIcon.ts`：尺寸、描边和无障碍属性。调整颜色或粗细时优先修改渲染层，保留已有轮廓。

社区仓库直接维护这些源文件。原始设计画板、独立 SVG 导出副本、PowerShell 导入脚本和临时预览页面不参与应用构建，已移出社区源码。

修改后运行 `npm run build`，在 `npm run tauri:dev` 中检查侧栏和曲目列表左侧导航的明暗主题、选中状态及英文标签。
