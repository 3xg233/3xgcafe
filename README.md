# 3xgcafe

由 bilibili@3xg233 制作的一些实用工具与网页的集合（部分借助 AI 构建）。

本仓库按**形态**分为两大目录：`desktop-apps/`（桌面小工具，C# / .NET 9 / WPF，仅 Windows）与 `web-apps/`（纯前端网页工具）。每个形态下再区分现行版与历史归档（`legacy/`）。

---

## [desktop-apps](desktop-apps/) —— 桌面小工具（现行版，v2.0）

统一命名为 `3xgcafe` 系列（显示名 `3xgcafe <功能名>`，程序集 `3xgcafe-<功能名>.exe`），各工具相互独立、可单独构建发布。

| 工具 | 说明 | 用户说明 | 维护手册 |
|------|------|----------|----------|
| **[3xgcafe Monitor](desktop-apps/MiniMonitor/)** | 桌面悬浮性能监控器（CPU / 内存 / 磁盘 / 温度 / 电池 / 网速） | [README](desktop-apps/MiniMonitor/README.md) | [MAINTENANCE](desktop-apps/MiniMonitor/MAINTENANCE.md) |
| **[3xgcafe Spotlight](desktop-apps/SpotlightLauncher/)** | 聚焦式应用启动器（含剪贴板历史） | [README](desktop-apps/SpotlightLauncher/README.md) | [MAINTENANCE](desktop-apps/SpotlightLauncher/MAINTENANCE.md) |
| **[3xgcafe Wallpaper](desktop-apps/WallpaperPure/)** | 一键隐藏 / 显示桌面图标的悬浮按钮 | [README](desktop-apps/WallpaperPure/README.md) | [MAINTENANCE](desktop-apps/WallpaperPure/MAINTENANCE.md) |
| **[3xgcafe Guard](desktop-apps/ScreenGuard/)** | 空闲自动锁定屏幕（毛玻璃锁屏 + 密码解锁） | [README](desktop-apps/ScreenGuard/README.md) | [MAINTENANCE](desktop-apps/ScreenGuard/MAINTENANCE.md) |
| **[3xgcafe Console](desktop-apps/3xgcafeConsole/)** | 统一管理面板：启停 / 自启 / 实时状态（命名管道通道） | [README](desktop-apps/3xgcafeConsole/README.md) | [MAINTENANCE](desktop-apps/3xgcafeConsole/MAINTENANCE.md) |

---

## [web-apps](web-apps/) —— 纯前端网页工具（现行版）

全部为单个 HTML 文件，浏览器打开即用，**无需安装、无需后端服务器**。

- **[歌词写作工具 lyrics](web-apps/lyrics/)** —— 面向音乐人的作词辅助工具（稳定版 + 实时协作 Beta 版）。
- **[音频提取工具 audio](web-apps/audio/)** —— 视频音频提取工具（ffmpeg.wasm 浏览器本地提取，Beta 版）。

---

## [legacy](legacy/) —— 历史归档

按形态拆分的历史版本线（[3xgcafe Console](desktop-apps/3xgcafeConsole/) 统一管理面板建立之前的版本），不再更新：

- 桌面工具：[legacy/desktop/](legacy/desktop/) —— MiniMonitor、SpotlightLauncher（已并入 [HydrargyrumLe](https://github.com/HydrargyrumLe) 的改良版）、WallpaperPure。
- 网页工具：[legacy/web/](legacy/web/) —— lyrics。

---

## 贡献者

- **3xgcafe** 由 bilibili@3xg233 创建并维护。
- **[HydrargyrumLe](https://github.com/HydrargyrumLe)** 为 MiniMonitor 与 SpotlightLauncher 贡献了改良版本（现分别收录于 3xgcafe Monitor 与 3xgcafe Spotlight）。

## 许可证

本仓库整体采用 [GPL-3.0](LICENSE)。

## 下载

- 桌面工具的**便携版**（单文件 exe 超过 100MB，不入库）请到 **[Releases](https://github.com/3xg233/3xgcafe/releases)** 页面获取。
- 网页工具直接打开 `web-apps/` 下对应的 HTML 文件即可，无需安装。
