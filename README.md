# 3xgcafe

由 bilibili@3xg233 制作的一些实用工具与网页的集合（部分借助 AI 构建）。

本仓库按**版本线**组织为两个目录：

---

## [Current version](Current%20version/) —— 现行版（v2.0）

统一命名为 `3xgcafe` 系列（显示名 `3xgcafe <功能名>`，程序集 `3xgcafe-<功能名>.exe`），各工具相互独立、可单独构建发布。

| 工具 | 说明 | 用户说明 | 维护手册 |
|------|------|----------|----------|
| **[3xgcafe Monitor](Current%20version/MiniMonitor/)** | 桌面悬浮性能监控器（CPU / 内存 / 磁盘 / 温度 / 电池 / 网速） | [README](Current%20version/MiniMonitor/README.md) | [MAINTENANCE](Current%20version/MiniMonitor/MAINTENANCE.md) |
| **[3xgcafe Spotlight](Current%20version/SpotlightLauncher/)** | 聚焦式应用启动器（含剪贴板历史） | [README](Current%20version/SpotlightLauncher/README.md) | [MAINTENANCE](Current%20version/SpotlightLauncher/MAINTENANCE.md) |
| **[3xgcafe Wallpaper](Current%20version/WallpaperPure/)** | 一键隐藏 / 显示桌面图标的悬浮按钮 | [README](Current%20version/WallpaperPure/README.md) | [MAINTENANCE](Current%20version/WallpaperPure/MAINTENANCE.md) |
| **[3xgcafe Guard](Current%20version/ScreenGuard/)** | 空闲自动锁定屏幕（毛玻璃锁屏 + 密码解锁） | [README](Current%20version/ScreenGuard/README.md) | [MAINTENANCE](Current%20version/ScreenGuard/MAINTENANCE.md) |
| **[3xgcafe Console](Current%20version/3xgcafeConsole/)** | 统一管理面板：启停 / 自启 / 实时状态（命名管道通道） | [README](Current%20version/3xgcafeConsole/README.md) | [MAINTENANCE](Current%20version/3xgcafeConsole/MAINTENANCE.md) |

网页工具（纯前端）：**[lyrics](Current%20version/lyrics/)** —— 面向音乐人的作词辅助工具（稳定版 + 实时协作 Beta 版）。

---

## [legacy](legacy/) —— Console 建立之前的版本线

作为历史归档保留，不再更新。含 MiniMonitor、SpotlightLauncher（已并入 [HydrargyrumLe](https://github.com/HydrargyrumLe) 的改良版）、WallpaperPure 与 lyrics。

---

## 贡献者

- **3xgcafe** 由 bilibili@3xg233 创建并维护。
- **[HydrargyrumLe](https://github.com/HydrargyrumLe)** 为 MiniMonitor 与 SpotlightLauncher 贡献了改良版本（现分别收录于 3xgcafe Monitor 与 3xgcafe Spotlight）。

## 许可证

本仓库整体采用 [GPL-3.0](LICENSE)。

## 下载

- 桌面工具的**便携版**（单文件 exe 超过 100MB，不入库）请到 **[Releases](https://github.com/3xg233/3xgcafe/releases)** 页面获取。
- 网页工具直接打开 `lyrics/` 下对应的 HTML 文件即可，无需安装。
