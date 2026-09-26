# 3xgcafe

> **版本线说明**：本目录（`legacy`）为 **3xgcafe Console 统一管理面板建立之前**的版本线，用于与引入控制面板之后的版本相区分。其中 MiniMonitor、SpotlightLauncher 已采用 [HydrargyrumLe](https://github.com/HydrargyrumLe) 的改良版本。
> 按形态拆分为 `desktop/`（桌面工具）与 `web/`（网页工具）两个子目录。

由 bilibili@3xg233 制作的一些实用工具与网页的集合（部分借助 AI 构建）。

本目录包含以下**相互独立**的历史项目：

## 桌面工具（C# / .NET 9 / WPF，仅 Windows）
- **[MiniMonitor](desktop/MiniMonitor/)** —— 桌面悬浮性能监控器，常驻显示 CPU / 内存 / 磁盘 / 温度等指标。
  - 用户说明：[desktop/MiniMonitor/README.md](desktop/MiniMonitor/README.md) ｜ 维护手册：[desktop/MiniMonitor/MAINTENANCE.md](desktop/MiniMonitor/MAINTENANCE.md)
- **[SpotlightLauncher](desktop/SpotlightLauncher/)** —— 类 macOS 聚焦的启动器，支持应用检索与剪贴板历史。
  - 用户说明：[desktop/SpotlightLauncher/README.md](desktop/SpotlightLauncher/README.md) ｜ 维护手册：[desktop/SpotlightLauncher/MAINTENANCE.md](desktop/SpotlightLauncher/MAINTENANCE.md)
- **[WallpaperPure](desktop/WallpaperPure/)** —— 一键隐藏/显示桌面图标的悬浮按钮（带淡入淡出过渡），方便纯净观赏壁纸。
  - 用户说明：[desktop/WallpaperPure/README.md](desktop/WallpaperPure/README.md) ｜ 维护手册：[desktop/WallpaperPure/MAINTENANCE.md](desktop/WallpaperPure/MAINTENANCE.md)

## 网页工具（纯前端）
- **[歌词写作工具 lyrics](web/lyrics/)** —— 面向音乐人的纯前端作词辅助工具（稳定版 + 实时协作 Beta 版）。
  - 说明：[web/lyrics/README.md](web/lyrics/README.md)

## 贡献者

- **3xgcafe** 由 bilibili@3xg233 创建并维护。
- **[HydrargyrumLe](https://github.com/HydrargyrumLe)** 为以下项目贡献了开发：
  - **[MiniMonitor](desktop/MiniMonitor/)** —— v1.3 的全部增强：窗口隐藏与全局热键 `Ctrl+Alt+M`、点击穿透、窗口透明度、显示指标自定义、电池与网速监控、GPU 顺序固定、磁盘盘符自动检测、多屏与 DPI 位置校验等。
  - **[SpotlightLauncher](desktop/SpotlightLauncher/)** —— v1.2 – v1.4：窗口拖动与位置记忆、手动添加扫描路径（对话框 / 拖放）、失活自动隐藏优化、应用图标统一。

## 许可证
本仓库整体采用 [GPL-3.0](../LICENSE)。

## 下载
- 桌面工具的便携版（exe / zip，单文件超过 100MB）请到 **[Releases](https://github.com/3xg233/3xgcafe/releases)** 页面获取。
- 网页工具直接打开 `web/lyrics/` 下对应的 HTML 文件即可，无需安装。
