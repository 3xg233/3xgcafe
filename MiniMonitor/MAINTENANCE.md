# MiniMonitor 维护手册

> 极简桌面悬浮性能监控器 · WPF / .NET 9 / Windows (win-x64)
> 本手册面向维护者与二次开发者，描述构建、运行、目录结构、关键实现与常见排查。本工具与 SpotlightLauncher **相互独立**，可单独构建、发布、迭代。

---

## 1. 概述

MiniMonitor 是一个常驻桌面角落的半透明悬浮小窗，黑底白线风格，一眼看清电脑状态。监控七项指标：

| 指标 | 含义 | 刷新频率 |
|------|------|----------|
| CPU  | 处理器总占用率 | 1 秒 |
| TMP  | CPU/ACPI 热区温度 | 5 秒 |
| GPU0 / GPU1 | 前两张显卡占用率（取最大引擎，口径同任务管理器） | 2 秒 |
| MEM  | 物理内存使用率 | 1 秒 |
| DSKC / DSKD | C 盘 / D 盘使用率（盘不存在显示 `--`） | 1 秒 |

- 数据全部本地采集，**不上传任何信息**。
- 显示 CPU 温度在部分主板/笔记本上需要**管理员权限**（普通权限时 ACPI 温度被拒，显示 `--`）。

---

## 2. 环境要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 / 11 x64 |
| 构建 | .NET 9 SDK（`dotnet` 在 PATH；本仓库原作者的 SDK 在 `~/.dotnet`，构建前需 `export PATH="$HOME/.dotnet:$PATH"`） |
| 运行（框架依赖版） | 目标机需安装 **.NET 9 桌面运行时** |
| 运行（自包含便携版） | 无需任何前置运行时 |

---

## 3. 目录结构

```
MiniMonitor/
├── MiniMonitor.csproj       # 项目文件：目标框架、单文件发布、运行时优化、NuGet 依赖
├── app.manifest             # 声明 PerMonitorV2 DPI 感知（WPF 字体清晰必需）
├── App.xaml / App.xaml.cs   # 应用入口：Mutex 互斥、托盘初始化
├── MainWindow.xaml          # 悬浮窗 UI 布局
├── MainWindow.xaml.cs       # 指标渲染、拖动、右键菜单、开机自启、设置读写
├── Monitor/
│   ├── AppSettings.cs       # 设置模型与持久化（位置 / 置顶 / 自启）
│   └── SysMonitor.cs        # 指标采集（CPU/温度/显卡/内存/磁盘）
├── Native/
│   ├── DisplayInfo.cs       # 多屏枚举、光标所在工作区、点是否在屏内
│   └── TrayIcon.cs          # Win32 Shell_NotifyIconW 托盘封装
├── README.md                # 用户向简介
└── MAINTENANCE.md           # 本文件
```

> 已**移除 WinForms** 依赖：托盘改用 `Shell_NotifyIconW`、屏幕定位改用 `user32` 的 `EnumDisplayMonitors`、消息框改用 WPF 原生。这显著降低了体积、启动与内存占用。

---

## 4. NuGet 依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `System.Management` | 9.0.2 | WMI 读取温度 / 磁盘信息 |
| `System.Diagnostics.PerformanceCounter` | 9.0.2 | CPU / 内存性能计数器 |
| `System.Drawing.Common` | 9.0.2 | GDI 绘制托盘图标（去 WinForms 后需显式引用） |

---

## 5. 构建

`MiniMonitor.csproj` 已内置 `PublishSingleFile=true`、`SelfContained=false`、`RuntimeIdentifier=win-x64` 和一系列运行时优化。

### 5.1 框架依赖版（输出 `dist/`，约 120 MB 单文件 exe）
目标机需装 .NET 9 桌面运行时；体积较小。

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

### 5.2 自包含便携版（输出 `dist-portable/`，约 120 MB 单文件 exe）
无需目标机运行时，可拷到任意 Windows x64 直接运行。

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -o dist-portable
```

> 因 exe 本身已是压缩单文件包，再 zip 体积几乎不降；发布到 GitHub Releases 时直接传 `*.exe` 或整体打包均可（单文件上限 2 GB）。

### 5.3 已启用的运行时优化（csproj）
- `ServerGarbageCollection=false`（桌面单进程用 Workstation GC 省内存）
- `ConcurrentGarbageCollection=true` / `TieredCompilation=true` / `TieredPGO=true`（提升启动）
- `RetainVM=false`（释放闲置虚拟内存）
- `ThreadPoolMinThreads=1` / `ThreadPoolMaxThreads=16`（限制线程数）

---

## 6. 运行

- 框架依赖版：双击 `dist/MiniMonitor.exe`（需 .NET 9 桌面运行时）。
- 便携版：双击 `dist-portable/MiniMonitor.exe`（无需运行时）。
- 需要 CPU 温度时：右键 → **以管理员身份运行**。
- 操作：
  - 左键按住拖动摆放位置（默认右上角）。
  - 右键菜单：窗口置顶 / 开机自启 / 复位到右上角 / 退出。
  - 系统托盘图标双击唤起窗口；右键提供同样菜单。

---

## 7. 数据存储

| 数据 | 路径 |
|------|------|
| 设置（位置 / 置顶 / 自启） | `%APPDATA%\MiniMonitor\settings.json` |

全部为本地文件，无网络访问。

---

## 8. 关键实现与维护注意

### 8.1 多实例互斥
应用启动时创建命名 Mutex `Local\MiniMonitor_Singleton`；若已存在则直接退出，保证只有一个悬浮窗。

### 8.2 开机自启（注册表方式）
在 **当前用户** 注册表键
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`
下写入值名 `MiniMonitor`，数据为本进程 exe 的完整路径（带引号）。取消勾选时删除该值。
> 注意：这与 SpotlightLauncher 的 JSON 自启方式**不同**，维护时不要混淆。

### 8.3 托盘
`Native/TrayIcon.cs` 用 `Shell_NotifyIconW` + 一个消息窗口实现；双击 `DoubleClick` 事件唤起主窗口，右键弹出 WPF `ContextMenu`。

### 8.4 DPI 感知
`app.manifest` 声明 `PerMonitorV2`。WPF 必须显式开启 DPI 感知，否则在多屏 / 缩放下字体与线宽发虚。

### 8.5 指标采集（`Monitor/SysMonitor.cs`）
- CPU：性能计数器，1 秒。
- 温度：WMI `ACPI` / 热区，5 秒；无权限或主板未暴露接口时返回 `--`。
- 显卡：GPU Engine 计数器，取最大引擎占用，2 秒。
- 内存：性能计数器，1 秒。
- 磁盘：逻辑盘使用率，1 秒；盘不存在显示 `--`。

### 8.6 多屏与窗口定位（`Native/DisplayInfo.cs`）
- `EnumDisplayMonitors` / `GetMonitorInfoW` 枚举显示器。
- `MonitorFromPoint` / `GetCursorPos` 定位光标所在工作区。
- `IsPointOnAnyScreen(x,y)` 判断窗口是否仍在屏内（用于复位兜底）。

---

## 9. 调试与验证注意事项

- **远程桌面环境**：原 `AllowTransparency=True` 分层窗口会导致白字 / 边框不渲染，已改为不透明窗口 + `Border` 描边。若改动透明度相关代码，请在远程桌面与本地各验证一次。
- **窗口初始定位**：`Show` 之前 `ActualWidth/ActualHeight` 为 0，初始定位必须用 `Width/Height`。
- **截图验证**：做 GUI 截图前需先 `SetProcessDpiAwarenessContext(PerMonitorV2)`，否则 DPI 坐标错位。
- **不要主动验证产物效果**：按项目约定，构建交付后由用户自行验证，维护者无需重复截图确认。

---

## 10. 常见问题

| 现象 | 原因 / 处理 |
|------|------|
| 温度显示 `--` | 普通权限被 ACPI 拒绝 → 以管理员身份运行；部分台式机主板未暴露热区接口，属正常。 |
| 悬浮窗挡住全屏 | 右键取消“窗口置顶”。 |
| 多实例无法启动 | 已有实例持有 `Local\MiniMonitor_Singleton`，关闭旧实例即可。 |
| 开机未自启 | 检查注册表 `HKCU\...\Run\MiniMonitor` 值是否存在且路径有效。 |

---

## 11. 版本与发布

- 产物体积：exe 约 120 MB（框架依赖与便携接近，因单文件已压缩）。
- 大文件（>100 MB）**不得**提交进 git；通过 **GitHub Releases** 分发。
- 提交源码时确保 `.gitignore` 已忽略 `bin/ obj/ dist/ dist-portable/ release/` 等构建产物。
