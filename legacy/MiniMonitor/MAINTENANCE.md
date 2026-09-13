# MiniMonitor 维护手册

> 极简桌面悬浮性能监控器 · WPF / .NET 9 / Windows (win-x64)
> 当前版本：**v2.0.0**
> 本手册面向维护者与二次开发者，描述构建、运行、目录结构、关键实现与常见排查。本工具与 SpotlightLauncher **相互独立**，可单独构建、发布、迭代。
> v1.3.0 的增强部分由 [HydrargyrumLe](https://github.com/HydrargyrumLe) 开发（详见 README 的「贡献者」一节）。

---

## 1. 概述

MiniMonitor 是一个常驻桌面角落的半透明悬浮小窗，黑底白线风格，一眼看清电脑状态。监控**十项**指标：

| 指标 | 含义 | 刷新频率 |
|------|------|----------|
| CPU  | 处理器总占用率 | 1 秒 |
| TMP  | ACPI 热区（主板区域）温度，**非 CPU 核心温度** | 5 秒 |
| GPU0 / GPU1 | 前两张显卡占用率（取最大引擎，口径同任务管理器；顺序固定，`GPU0/GPU1` 对应系统适配器 0/1） | 2 秒 |
| MEM  | 物理内存使用率 | 1 秒 |
| DSKC / DSKD | 磁盘使用率（默认自动检测前两个固定盘并按实际盘符显示标签，如 `DSKC`/`DSKD`；盘不存在显示 `--`） | 1 秒 |
| BAT  | 电池电量（充电时显示 `87%+`；台式机 / 无电池显示 `--`） | 5 秒 |
| RX / TX | 物理网卡（有线 / 无线 / PPP）下载 / 上传速率，自适应 `B/K/M`；进度条按 100 Mbps 满格 | 1 秒 |

- 数据全部本地采集，**不上传任何信息**。
- 窗口宽度固定 `240`，高度 `SizeToContent="Height"`：隐藏的指标行不占空间，窗口自动收缩。
- 温度优先使用**普通权限可读的热区计数器**，失败时再尝试需管理员权限的 `MSAcpi`；两者都不可用时显示 `--`。

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
├── MiniMonitor.csproj       # 项目文件：目标框架、单文件发布、运行时优化、NuGet 依赖（Version 1.3.0）
├── app.manifest             # 声明 PerMonitorV2 DPI 感知（WPF 字体清晰必需）
├── App.xaml / App.xaml.cs   # 应用入口：Mutex 互斥、托盘初始化、托盘菜单（与窗口菜单共用子菜单）
├── MainWindow.xaml          # 悬浮窗 UI 布局（Width=240 / SizeToContent=Height）
├── MainWindow.xaml.cs       # 指标渲染、拖动、右键菜单、热键、穿透、透明度、指标可见性、设置读写
├── Monitor/
│   ├── AppSettings.cs       # 设置模型与持久化（位置/置顶/自启/穿透/透明度/盘符/可见指标）
│   └── SysMonitor.cs        # 指标采集（CPU/温度/GPU/内存/磁盘/电池/网速）
├── Native/
│   ├── DisplayInfo.cs       # 多屏枚举、光标所在工作区、点/窗口是否在屏内
│   ├── Dxgi.cs              # DXGI 枚举物理显卡（LUID / 名称 / 系统适配器序号）
│   ├── TrayIcon.cs          # Win32 Shell_NotifyIconW 托盘封装
│   └── WindowNative.cs      # 点击穿透（WS_EX_TRANSPARENT）+ 全局热键（RegisterHotKey）
├── README.md                # 用户向简介
└── MAINTENANCE.md           # 本文件
```

> 已**移除 WinForms** 依赖：托盘改用 `Shell_NotifyIconW`、屏幕定位改用 `user32` 的 `EnumDisplayMonitors`、消息框改用 WPF 原生。这显著降低了体积、启动与内存占用。

---

## 4. NuGet 依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `System.Management` | 9.0.2 | WMI 读取温度 / GPU 信息 |
| `System.Diagnostics.PerformanceCounter` | 9.0.2 | CPU / 内存性能计数器 |
| `System.Drawing.Common` | 9.0.2 | GDI 绘制托盘图标（去 WinForms 后需显式引用） |

> 电池走 `kernel32!GetSystemPowerStatus`、网速走 `System.Net.NetworkInformation`，均无需额外 NuGet。

---

## 5. 构建

`MiniMonitor.csproj` 已内置 `PublishSingleFile=true`、`SelfContained=false`、`RuntimeIdentifier=win-x64` 和一系列运行时优化。

### 5.1 框架依赖版（输出 `dist/`，约 120 MB 单文件 exe）
目标机需装 .NET 9 桌面运行时；体积较小。

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

### 5.2 自包含便携版（输出 `dist-portable/`，约 126 MB 单文件 exe）
无需目标机运行时，可拷到任意 Windows x64 直接运行。

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:SatelliteResourceLanguages=en \
  -o dist-portable
```

> ⚠️ **`-o` 必须使用 Windows 风格路径**（如 `C:\...\dist-portable`）。在 Git Bash 下传 `/c/...` 会被 dotnet 解释成 `C:\c\...`，产物落到错误目录。
> exe 本身已是压缩单文件包，再 zip 体积几乎不降；发布到 GitHub Releases 时直接传 `*.exe` 即可（单文件上限 2 GB）。

### 5.3 已启用的运行时优化（csproj）
- `ServerGarbageCollection=false`（桌面单进程用 Workstation GC 省内存）
- `ConcurrentGarbageCollection=true` / `TieredCompilation=true` / `TieredPGO=true`（提升启动）
- `RetainVM=false`（释放闲置虚拟内存）
- `ThreadPoolMinThreads=1` / `ThreadPoolMaxThreads=16`（限制线程数）
- `IncludeNativeLibrariesForSelfExtract=true`（v1.3 新增：把 WPF 原生库打进单文件 exe，缺少时单独拷走的 exe 会因缺 DLL 闪退）

---

## 6. 运行

- 框架依赖版：双击 `dist/MiniMonitor.exe`（需 .NET 9 桌面运行时）。
- 便携版：双击 `dist-portable/MiniMonitor.exe`（无需运行时）。
- 操作：
  - **左键按住拖动**摆放位置（默认右上角；拖拽结束后会钳回虚拟屏幕范围内）。
  - **全局热键 `Ctrl+Alt+M`**：显示 / 隐藏窗口（被其他程序占用时静默失效）。
  - **右键菜单**：窗口置顶 / 开机自启 / 点击穿透 / 显示指标（10 项可勾选）/ 窗口透明度（5 档）/ 复位到屏幕右上角 / 隐藏窗口 / 退出。
  - **系统托盘**：双击唤起窗口；右键提供 显示/隐藏窗口、置顶、开机自启、点击穿透、显示指标、窗口透明度、复位、退出。
- 开启「点击穿透」后窗口不再响应鼠标（不能拖动 / 右键），需从托盘菜单关闭。

---

## 7. 数据存储

| 数据 | 路径 |
|------|------|
| 设置 | `%APPDATA%\MiniMonitor\settings.json` |
| 崩溃日志 | `%APPDATA%\MiniMonitor\crash.log`（异常时写入） |

`settings.json` 键位：

| 键 | 类型 | 说明 |
|----|------|------|
| `X` / `Y` | double? | 窗口左上角坐标 |
| `Topmost` | bool | 窗口置顶（默认 false） |
| `AutoStart` | bool | 开机自启 |
| `ClickThrough` | bool | 点击穿透 |
| `Opacity` | double | 窗口透明度（0.4 ~ 1.0） |
| `Drives` | string[]? | 两行磁盘指标的盘符（如 `["C","E"]`）；`null` 时自动检测 |
| `VisibleMetrics` | string[]? | 可见指标 id（如 `["cpu","mem","rx"]`）；`null` 表示全部显示 |

> 向后兼容：旧版 `settings.json` 缺少 `ClickThrough` / `Opacity` / `Drives` / `VisibleMetrics` 时按默认值处理。

全部为本地文件，无网络访问。

---

## 8. 关键实现与维护注意

### 8.1 多实例互斥
应用启动时创建命名 Mutex `Local\MiniMonitor_Singleton`；若已存在则直接退出，保证只有一个悬浮窗。

### 8.2 开机自启（注册表方式）
在 **当前用户** 注册表键
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`
下写入值名 `MiniMonitor`，数据为本进程 exe 的完整路径（带引号）。取消勾选时删除该值。
> 注意：SpotlightLauncher 同样使用注册表方式，但值名不同（`SpotlightLauncher`），维护时不要混淆。

### 8.3 托盘
`Native/TrayIcon.cs` 用 `Shell_NotifyIconW` + 一个消息窗口实现；双击 `DoubleClick` 事件唤起/隐藏窗口，右键弹出 WPF `ContextMenu`。「显示指标」「窗口透明度」子菜单由 `MainWindow` 以 `public` 方法创建，窗口菜单与托盘菜单共用同一份构建逻辑。

### 8.4 DPI 感知
`app.manifest` 声明 `PerMonitorV2`。WPF 必须显式开启 DPI 感知，否则在多屏 / 缩放下字体与线宽发虚。

### 8.5 指标采集（`Monitor/SysMonitor.cs`）
- CPU：性能计数器，1 秒。
- 温度：热区计数器优先（普通权限可读），失败回退 `MSAcpi`（需管理员权限），5 秒；两者都不可用返回 `--`。**兼容 0.1°C 与 0.1K 两种固件上报格式**。
- 显卡：GPU Engine 计数器取最大引擎占用，2 秒。
- 内存：性能计数器，1 秒。
- 磁盘：`Disk0/Disk1`（对应 `Drive0Letter` / `Drive1Letter`），1 秒。
- 电池：`GetSystemPowerStatus`，5 秒；`BatteryCharging` 决定是否显示 `%+`。
- 网速：`NetworkInterface` 过滤出物理网卡（Ethernet / Wireless80211 / PPP），按 IP 层字节差值得出 `RxBytesPerSec` / `TxBytesPerSec`，1 秒。

### 8.6 GPU 行顺序固定（`Native/Dxgi.cs`）
通过 DXGI COM 枚举物理显卡，过滤软件渲染器，得到 LUID、显卡名与**系统适配器顺序**。`GPU0`/`GPU1` 固定对应系统适配器 0/1，不再随 WMI 枚举顺序互换（重装驱动 / 换机后行序稳定）。GPU 标签悬停显示完整显卡名。

### 8.7 磁盘盘符自动检测
默认自动检测**前两个就绪的固定盘**并按字母排序，标签随实际盘符显示；可在 `settings.json` 用 `Drives` 手动指定（1~2 个盘符字母，重启生效）。

### 8.8 点击穿透（`Native/WindowNative.cs`）
对窗口追加 `WS_EX_TRANSPARENT` 扩展样式，并用 `SetWindowPos(... SWP_FRAMECHANGED)` 通知窗口管理器重算命中测试。开启后鼠标事件穿透到下层窗口，只能从托盘菜单关闭。

### 8.9 全局热键
`RegisterHotKey(hwnd, HotkeyId=0x4D01, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M)`，在 `OnSourceInitialized` 注册、`OnClosed` 注销。被其他程序占用时静默失效（`RegisterHotKey` 返回 false）。

### 8.10 显示指标自定义与窗口高度自适应
- `_metrics` 注册 10 项指标（id → 名称 → 行），`_visibleMetrics` 记录可见集合。
- 至少保留 1 项可见：`ToggleMetric` 在只剩 1 项时静默忽略取消操作。
- 行定义使用 `Auto` + 窗口 `SizeToContent="Height"`，`Visibility.Collapsed` 的行不占空间。
- 全部可见时 `VisibleMetrics` 存 `null`，保持配置文件简洁。

### 8.11 多屏与窗口定位（`Native/DisplayInfo.cs`）
- `EnumDisplayMonitors` / `GetMonitorInfoW` 枚举显示器。
- `MonitorFromPoint` / `GetCursorPos` 定位光标所在工作区。
- `IsPointOnAnyScreen(x,y)` / `IsWindowOnAnyScreen(hwnd)` 判断是否仍在屏内。
- 启动用 `IsPointOnAnyScreen` 预检，窗口 `Loaded` 后再用 `IsWindowOnAnyScreen` 由系统权威校验一次，不在任何屏幕时复位到右上角。
- 拖拽结束后 `ClampWindowToScreen()` 把窗口钳回虚拟屏幕范围。

### 8.12 窗口透明度
五档 `{1.0, 0.85, 0.70, 0.55, 0.40}`，窗口菜单与托盘菜单共用 `CreateOpacityMenu`，选择后写入 `settings.json` 的 `Opacity`。

---

## 9. 调试与验证注意事项

- **远程桌面环境**：原 `AllowTransparency=True` 分层窗口会导致白字 / 边框不渲染，已改为不透明窗口 + `Border` 描边。若改动透明度相关代码，请在远程桌面与本地各验证一次。
- **窗口初始定位**：`Show` 之前 `ActualWidth/ActualHeight` 为 0，初始定位必须用 `Width/Height`。注意本版本启用了 `SizeToContent="Height"`，`ActualHeight` 会随可见指标变化，**不要**用它做定位基准。
- **截图验证**：做 GUI 截图前需先 `SetProcessDpiAwarenessContext(PerMonitorV2)`，否则 DPI 坐标错位。
- **不要主动验证产物效果**：按项目约定，构建交付后由用户自行验证，维护者无需重复截图确认。

### 已知遗留（暂不影响使用）
1. `TMP` 是 ACPI 主板热区温度，**不是 CPU 核心温度**，固件差异大，读数仅供参考。
2. WMI GPU 查询每 2 秒一次（本机可枚举出数百个引擎实例），`WmiPrvSE` 有少量常驻开销；如需降耗可降频或改用性能计数器。
3. `SysMonitor.Dispose` 的 `Thread.Interrupt` 命中 `Sleep` 时，会往 `crash.log` 写一条无害的假崩溃记录。
4. 表现良好、未改动的部分：托盘（`Shell_NotifyIconW` V3）、单实例 Mutex、HKCU Run 自启、PerMonitorV2 DPI。

---

## 10. 常见问题

| 现象 | 原因 / 处理 |
|------|------|
| 温度显示 `--` | 两种数据源（热区计数器 / MSAcpi）均不可用；可尝试以管理员身份运行。TMP 为主板区域温度，属正常口径。 |
| 悬浮窗挡住全屏 | 右键取消「窗口置顶」。 |
| 窗口点不动、无法右键 | 误开启了「点击穿透」→ 从托盘菜单关闭。 |
| 少了一行指标 | 被「显示指标」隐藏 → 右键菜单重新勾选。 |
| 换了硬盘 / 加了盘，磁盘行不对 | 在 `settings.json` 用 `Drives` 指定盘符，或删除该项使用自动检测。 |
| 多实例无法启动 | 已有实例持有 `Local\MiniMonitor_Singleton`，关闭旧实例即可。 |
| 开机未自启 | 检查注册表 `HKCU\...\Run\MiniMonitor` 值是否存在且路径有效。 |
| 外接屏拔掉后窗口消失 | 程序会在启动 / 显示时自动校验并复位到右上角；也可用「复位到屏幕右上角」。 |

---

## 11. 版本与发布

- 当前版本：**v2.0.0**（`MiniMonitor.csproj` 的 `Version` / `FileVersion`）。
- 产物体积：exe 约 126 MB（框架依赖与便携接近，因单文件已压缩）。
- 大文件（>100 MB）**不得**提交进 git；通过 **GitHub Releases** 分发。
- 提交源码时确保 `.gitignore` 已忽略 `bin/ obj/ dist/ dist-portable/ release/` 等构建产物。
