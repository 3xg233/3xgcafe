# SpotlightLauncher 维护手册

> Spotlight 风格应用启动器（含剪贴板历史） · WPF / .NET 9 / Windows (win-x64)
> 本手册面向维护者与二次开发者，描述构建、运行、目录结构、关键实现与常见排查。本工具与 MiniMonitor **相互独立**，可单独构建、发布、迭代。

---

## 1. 概述

SpotlightLauncher 是按下全局热键即呼出的聚焦式搜索启动器，UI 复刻《终末地》游戏语言（黑灰底 + 荧光黄绿 RGB(250,252,82) + 等高线 + 工业终端标题栏）。

两大功能：
1. **应用查找**（App 模式，默认）：索引开始菜单 / UWP / 桌面快捷方式 / Program Files / Steam / Epic，输入即过滤，支持拼音首字母。
2. **剪贴板历史**（Clip 模式，v1.2 新增）：监听系统剪贴板，支持模糊搜索与按时间回溯；窗口内按 `Alt` 切换模式。

- 启动后常驻系统托盘；全局热键 `Ctrl+Alt+Space` 唤起。
- 全部数据本地存储，**不上传任何信息**。

---

## 2. 环境要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 19041+（项目目标框架 `net9.0-windows10.0.19041.0`，需对应 Windows SDK） |
| 构建 | .NET 9 SDK（`dotnet` 在 PATH；原作者 SDK 在 `~/.dotnet`，构建前需 `export PATH="$HOME/.dotnet:$PATH"`） |
| 运行（框架依赖版） | 目标机需安装 **.NET 9 桌面运行时** |
| 运行（自包含便携版） | 无需任何前置运行时 |

---

## 3. 目录结构

```
SpotlightLauncher/
├── SpotlightLauncher.csproj   # 项目文件：目标框架、单文件发布、运行时优化、NuGet 依赖
├── app.manifest               # PerMonitorV2 DPI 感知
├── App.xaml / App.xaml.cs     # 入口：Mutex 互斥、全局热键注册、托盘、自启偏好
├── MainWindow.xaml            # 搜索框 / 模式标签页 / 结果列表 / 右键菜单 UI
├── MainWindow.xaml.cs         # 搜索过滤、模式切换(Alt)、剪贴板监听与捕获、结果交互
├── Indexing/
│   ├── AppEntry.cs            # 索引条目模型（路径 / 参数 / 名称 / 图标 / 来源）
│   ├── AppIndexer.cs          # 后台索引构建与相似度排序
│   └── PinyinHelper.cs        # 拼音首字母 / 全拼转换
├── Clipboard/
│   └── ClipboardStore.cs      # 剪贴板历史：内存列表 + 磁盘持久化 + 模糊搜索
├── Native/
│   ├── NativeMethods.cs       # Win32 声明：RegisterHotKey / 剪贴板监听 / 消息常量
│   ├── DisplayInfo.cs         # 多屏枚举、光标所在工作区
│   └── TrayIcon.cs            # Win32 Shell_NotifyIconW 托盘封装
├── README.md                  # 用户向简介
└── MAINTENANCE.md             # 本文件
```

> 已**移除 WinForms** 依赖：托盘用 `Shell_NotifyIconW`、屏幕 / 鼠标用 `user32`、消息框用 WPF 原生。这降低了体积、启动与内存占用。

---

## 4. NuGet 依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `PinYinConverterCore` | 1.0.2 | 中文名 → 拼音首字母 / 全拼，支撑拼音搜索 |
| `System.Drawing.Common` | 9.0.2 | GDI 绘制托盘图标、剪贴板图片缩略图（去 WinForms 后需显式引用） |

---

## 5. 构建

`SpotlightLauncher.csproj` 已内置 `PublishSingleFile=true`、`SelfContained=false`、`RuntimeIdentifier=win-x64` 和运行时优化。

### 5.1 框架依赖版（输出 `dist/`，约 145 MB 单文件 exe）
目标机需装 .NET 9 桌面运行时；体积较小。

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

### 5.2 自包含便携版（输出 `dist-portable/`，约 145 MB 单文件 exe）
无需目标机运行时，可拷到任意 Windows x64 直接运行。

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -o dist-portable
```

> exe 本身已是压缩单文件包，再 zip 体积几乎不降；发布到 GitHub Releases 时直接传 `*.exe` 或整体打包均可（单文件上限 2 GB）。

### 5.3 已启用的运行时优化（csproj）
与 MiniMonitor 一致：`ServerGarbageCollection=false`、`ConcurrentGarbageCollection=true`、`TieredCompilation=true`、`TieredPGO=true`、`RetainVM=false`、`ThreadPoolMinThreads=1`、`ThreadPoolMaxThreads=16`。

---

## 6. 运行

- 框架依赖版：双击 `dist/SpotlightLauncher.exe`。
- 便携版：双击 `dist-portable/SpotlightLauncher.exe`。
- 全局热键 `Ctrl+Alt+Space` 唤起；托盘图标双击亦可打开；`Esc` 或点击窗口外部关闭。
- **调试参数**：`--no-autohide` —— 失活时窗口不自动隐藏（自动化测试用，普通用户无需）。

---

## 7. 数据存储

| 数据 | 路径 | 说明 |
|------|------|------|
| 剪贴板历史 | `%APPDATA%\SpotlightLauncher\clipboard\history.json` + `images/{id}.png` | 文本存 JSON；图片存 PNG 缩略原图 |
| 使用频率 | `%APPDATA%\SpotlightLauncher\freq.json` | 常用应用加权排序 |
| 自启偏好 | `%APPDATA%\SpotlightLauncher\autostart.json` | `{"AutoStart": bool}` |

> ⚠️ **与 MiniMonitor 不同**：本工具的开机自启走 **JSON 配置文件**而非注册表。维护时不要套用 MiniMonitor 的注册表逻辑。

---

## 8. 关键实现与维护注意

### 8.1 多实例互斥
启动时创建命名 Mutex `Local\SpotlightLauncher_Singleton`；已存在则退出，保证单一实例。

### 8.2 全局热键
- `RegisterHotKey(hwnd, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE)`。
- 窗口从不主动显示，需在 `SourceInitialized` 前用 `EnsureHandle` 预创建 HWND，否则热键无法注册。
- 注册失败（如被其他程序占用）时弹托盘提示，用户可双击托盘图标打开。

### 8.3 开机自启（JSON 方式）
- `App.xaml.cs` 中 `AutoStartSettingsFile` 指向 `autostart.json`。
- `LoadAutoStartPref` / `SaveAutoStartPref` / `ToggleAutoStart` 读写该文件；托盘菜单“开机自启”可勾选切换。
- 当前实现仅记录偏好，**真正自启的落地（如写入注册表或计划任务）需另行接入**；如需与 MiniMonitor 行为对齐，可参考其注册表方案。

### 8.4 托盘
`Native/TrayIcon.cs` 用 `Shell_NotifyIconW` + 消息窗口；双击打开启动器，右键弹出 WPF `ContextMenu`（含开机自启、退出）。

### 8.5 应用索引（`Indexing/AppIndexer.cs`）
- **范围**：开始菜单快捷方式 + UWP/商店应用（`PackageManager`）+ 桌面 `.lnk`/`.url` + `Program Files(x86)`/`Program Files` 全盘 `.exe` + Steam 库（`libraryfolders.vdf` + `appmanifest_*.acf`，用 `steam://rungameid`）+ Epic 清单。
- **噪声过滤**：系统工具 / 卸载程序 / 管家助手等被剔除。
- **去重**：去重键为「目标路径 + 参数」（同 exe 不同参数视为不同条目）。
- **排序**：相似度 = Dice bigram + 子串 + 拼音首字母/全拼 + 文件名 + 包名 + 前缀/频率加权；常用项自动靠前（`freq.json`）。
- **构建**：后台约 1 秒；期间搜索可能为空，稍后自动就绪。

### 8.6 拼音（`Indexing/PinyinHelper.cs`）
基于 `PinYinConverterCore` 将中文名转为拼音首字母与全拼，使 `wx` → 微信、`jsq` → 计算器。

### 8.7 剪贴板历史（v1.2）
- **两种模式**：`App`（软件查找，默认）/ `Clip`（剪贴板历史）。呼出时默认 App 模式；窗口内按 **`Alt`** 切换（标题栏 Tab 高亮同步）。`Alt` 检测用 `Key.System` + 左/右 Alt 且排除 Ctrl/Shift，配 `_altDown` 防抖。
- **监听**：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE(0x031D)`，收到后 200 ms 防抖再读取，避免高频拷贝风暴。
- **捕获**：图片优先于文本；文本取 `UnicodeText`；读写异常（`ExternalException`）安全忽略。
- **搜索**：
  - 无关键词 → 按时间倒序展示全部；
  - 有关键词 → 模糊匹配：`子串` > `最长公共子串覆盖(≥0.3–0.45)` > `多词命中`。
- **图片**：缩略图 `DecodePixelWidth=40`；存储为 `images/{id}.png`。
- **容量上限**（`Clipboard/ClipboardStore.cs`）：
  - `MaxTotalItems = 400`（总条数）
  - `MaxImageItems = 60`（图片条数）
  - `MaxTextLength = 4000`（单条文本裁剪上限，控内存与检索成本）
- **右键菜单快捷键**（软件模式指向文件时有效）：
  - `Ctrl+Shift+F`：打开文件所在位置
  - `Ctrl+Shift+C`：复制路径 / 文本
  - `Enter`：启动应用 / 将剪贴板条目复制回系统剪贴板

---

## 9. 调试与验证注意事项

- **类型歧义**：因同时引用 `System.Drawing` 与 `System.Windows.Media`，`Brush`/`Color`/`Pen` 需用完全限定名（GDI 用 `System.Drawing.*`，WPF 用 `System.Windows.Media.*`）。新增绘图代码时注意。
- **远程桌面环境**：原 `AllowTransparency=True` 分层窗口会导致白字 / 边框不渲染，已改为不透明窗口 + `Border` 描边。改动透明度相关代码请在远程桌面与本地各验证一次。
- **窗口初始定位**：`Show` 之前 `ActualWidth/ActualHeight` 为 0，初始定位必须用 `Width/Height`。
- **截图验证**：做 GUI 截图前需先 `SetProcessDpiAwarenessContext(PerMonitorV2)`，否则 DPI 坐标错位。
- **不要主动验证产物效果**：按项目约定，构建交付后由用户自行验证，维护者无需重复截图确认。

---

## 10. 常见问题

| 现象 | 原因 / 处理 |
|------|------|
| 热键无反应 | `Ctrl+Alt+Space` 被其他程序占用 → 看托盘提示，双击托盘图标打开；或换用双击托盘。 |
| 搜不到某应用 | 少数 UWP 未暴露 `AppListEntry`，或应用未装到当前用户，属正常。 |
| 启动瞬间搜索为空 | 索引后台构建约 1 秒，稍候自动就绪。 |
| 多实例无法启动 | 已有实例持有 `Local\SpotlightLauncher_Singleton`，关闭旧实例即可。 |
| 剪贴板模式不更新 | 确认系统剪贴板格式被监听（`WM_CLIPBOARDUPDATE`）；图片复制应出现缩略图。 |

---

## 11. 版本与发布

- 产物体积：exe 约 145 MB（框架依赖与便携接近）。
- 大文件（>100 MB）**不得**提交进 git；通过 **GitHub Releases** 分发。
- 提交源码时确保 `.gitignore` 已忽略 `bin/ obj/ dist/ dist-portable/ release/` 等构建产物。
