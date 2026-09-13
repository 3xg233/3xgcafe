# SpotlightLauncher 维护手册

> Spotlight 风格应用启动器（含剪贴板历史） · WPF / .NET 9 / Windows (win-x64)
> 当前版本：**v2.0.0**
> 本手册面向维护者与二次开发者，描述构建、运行、目录结构、关键实现与常见排查。本工具与 MiniMonitor **相互独立**，可单独构建、发布、迭代。
> v1.2 – v1.4 的增强部分由 [HydrargyrumLe](https://github.com/HydrargyrumLe) 开发（详见 README 的「贡献者」一节）。

---

## 1. 概述

SpotlightLauncher 是按下全局热键即呼出的聚焦式搜索启动器，UI 复刻《终末地》游戏语言（黑灰底 + 荧光黄绿 RGB(250,252,82) + 等高线 + 工业终端标题栏）。

两大功能：
1. **应用查找**（App 模式，默认）：索引开始菜单 / UWP / 桌面快捷方式 / Program Files / Steam / Epic，输入即过滤，支持拼音首字母。
2. **剪贴板历史**（Clip 模式，v1.2 引入）：监听系统剪贴板，支持模糊搜索与按时间回溯；窗口内按 `Alt` 切换模式。

版本演进：

| 版本 | 主题 |
|------|------|
| 初版 | 终末地风格启动器 + 剪贴板历史 |
| **v1.2** | 窗口拖动与位置记忆 + 手动添加扫描路径 |
| **v1.3** | 失活自动隐藏体验优化（拖拽豁免 / 保持窗口开关 / 热键开关） |
| **v1.4** | 应用图标统一（单个放大镜）+ 死代码清理 |
| **v2.0.0** | 统一版本号，与 3xgcafe 全系对齐 |

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
├── SpotlightLauncher.csproj   # 项目文件：目标框架、单文件发布、运行时优化、NuGet 依赖（Version 1.4.0、ApplicationIcon）
├── app.manifest               # PerMonitorV2 DPI 感知
├── Assets/
│   ├── app.ico                # 应用图标（单个放大镜：荧光黄描边 + 深色圆角底）
│   └── preview/               # 各尺寸图标预览图（16/48/256）
├── App.xaml / App.xaml.cs     # 入口：Mutex 互斥、全局热键、托盘与托盘菜单、自启与偏好读写、崩溃日志
├── MainWindow.xaml            # 搜索框 / 模式标签页 / 结果列表 / 状态条 / 右键菜单 UI
├── MainWindow.xaml.cs         # 搜索过滤、模式切换(Alt)、剪贴板监听与捕获、窗口拖动与位置记忆、拖放、失活自动隐藏
├── PathsDialog.xaml(.cs)      # 「+ 路径」扫描路径管理对话框（添加 exe/文件夹、删除、Esc 关闭）
├── Indexing/
│   ├── AppEntry.cs            # 索引条目模型（路径 / 参数 / 名称 / 图标 / 来源）
│   ├── AppIndexer.cs          # 后台索引构建与相似度排序
│   ├── PinyinHelper.cs        # 拼音首字母 / 全拼转换
│   └── ScanPathsStore.cs      # 自定义扫描路径（scanpaths.json）的存储与增删
├── Clipboard/
│   └── ClipboardStore.cs      # 剪贴板历史：内存列表 + 磁盘持久化 + 模糊搜索
├── Native/
│   ├── NativeMethods.cs       # Win32 声明：RegisterHotKey / 剪贴板监听 / 鼠标键状态 / 消息常量
│   ├── DisplayInfo.cs         # 多屏枚举、点是否在任一屏幕内
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

### 5.1 框架依赖版（输出 `dist/`，约 26 MB 单文件 exe）
目标机需装 .NET 9 桌面运行时；体积较小。

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

### 5.2 自包含便携版（输出 `dist-portable/`，约 152 MB 单文件 exe）
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
> 发布到 GitHub Releases 时直接传 `*.exe` 即可（单文件上限 2 GB）。

### 5.3 已启用的运行时优化（csproj）
与 MiniMonitor 一致：`ServerGarbageCollection=false`、`ConcurrentGarbageCollection=true`、`TieredCompilation=true`、`TieredPGO=true`、`RetainVM=false`、`ThreadPoolMinThreads=1`、`ThreadPoolMaxThreads=16`。

---

## 6. 运行

- 框架依赖版：双击 `dist/SpotlightLauncher.exe`。
- 便携版：双击 `dist-portable/SpotlightLauncher.exe`。
- 全局热键 `Ctrl+Alt+Space`：**窗口可见时按下＝收起，隐藏时按下＝呼出**（v1.3 起为显隐开关）；托盘图标双击亦可打开；`Esc` 关闭。
- 按住**标题行**可拖动窗口，位置自动记忆；鼠标左键按住（拖拽进行中）期间窗口不会自动隐藏。
- **调试参数**：`--no-autohide` —— 失活时窗口不自动隐藏（自动化测试用，普通用户无需）。

---

## 7. 数据存储

| 数据 | 路径 | 说明 |
|------|------|------|
| 剪贴板历史 | `%APPDATA%\SpotlightLauncher\clipboard\history.json` + `images/{id}.png` | 文本存 JSON；图片存 PNG |
| 使用频率 | `%APPDATA%\SpotlightLauncher\freq.json` | 常用应用加权排序 |
| 偏好设置 | `%APPDATA%\SpotlightLauncher\autostart.json` | `{"AutoStart": bool, "KeepOnDeactivate": bool}` 双键 |
| 窗口位置 | `%APPDATA%\SpotlightLauncher\window.json` | `{"Left": double, "Top": double}` |
| 自定义扫描路径 | `%APPDATA%\SpotlightLauncher\scanpaths.json` | 字符串数组；兼容旧对象格式读取 |
| 崩溃日志 | `%APPDATA%\SpotlightLauncher\crash.log` | AppDomain / Dispatcher 未处理异常 |

> ⚠️ **自启状态的真源是注册表**（`HKCU\...\Run\SpotlightLauncher`）；`autostart.json` 只记录用户的**偏好**，用于避免"取消勾选后被静默恢复"。两者需成对维护。

---

## 8. 关键实现与维护注意

### 8.1 多实例互斥
启动时创建命名 Mutex `Local\SpotlightLauncher_Singleton`；已存在则提示并退出，保证单一实例。

### 8.2 全局热键
- `RegisterHotKey(handle, HotKeyId=1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE)`。
- 窗口从不主动显示，需在 `SourceInitialized` 前用 `EnsureHandle` 预创建 HWND，否则热键无法注册。
- v1.3 起热键为**可见性开关**：`WndProc` 收到 `WM_HOTKEY` 时，窗口可见则 `HideLauncher()`，否则 `ShowLauncher()`。
- 注册失败（如被其他程序占用）时弹托盘气泡提示，用户可双击托盘图标打开。

### 8.3 开机自启（注册表 + 偏好双写）
- 真源：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的值名 `SpotlightLauncher`，数据为 exe 完整路径。
- 偏好：`autostart.json` 的 `AutoStart` 键。首次运行无该键时**默认开启并落盘**。
- `ToggleAutoStart()` 同时改注册表与偏好，保证二者一致。

### 8.4 「失活时保持窗口」偏好
- `autostart.json` 的 `KeepOnDeactivate` 键（缺键默认 false，即保持原有"失活 500ms 后自动隐藏"行为）。
- 勾选后点击外部 / 切到其他应用不再收起，只有 `Esc` 或热键能关。
- `MainWindow.KeepOnDeactivate` 的 setter 在关闭时会补一次 `EvaluateAutoHide()`，立即恢复隐藏节奏。

### 8.5 托盘
`Native/TrayIcon.cs` 用 `Shell_NotifyIconW` + 消息窗口；双击打开启动器，右键菜单含「打开启动器 / 开机自启 / 失活时保持窗口 / 退出」，菜单 `Opened` 时同步勾选状态。托盘图标由 GDI+ 现绘（荧光黄放大镜，与应用图标一致）。

### 8.6 应用索引（`Indexing/AppIndexer.cs`）
- **范围**：自定义路径（`scanpaths.json`）→ 开始菜单快捷方式 + UWP/商店应用（`PackageManager`）+ 桌面 `.lnk`/`.url` + `Program Files(x86)`/`Program Files` 全盘 `.exe` + Steam 库（`libraryfolders.vdf` + `appmanifest_*.acf`，用 `steam://rungameid`）+ Epic 清单。
- **顺序注意**：自定义路径**先于**批量扫描执行。原顺序下 Program Files 可能先把 2500 条上限占满，导致手动添加的路径被静默丢弃。
- **噪声过滤**：系统工具 / 卸载程序 / 管家助手等被剔除。
- **去重**：去重键为「目标路径 + 参数」（同 exe 不同参数视为不同条目）。
- **排序**：相似度 = Dice bigram + 子串 + 拼音首字母/全拼 + 文件名 + 包名 + 前缀/频率加权；常用项自动靠前（`freq.json`）。
- **构建**：后台约 1 秒；期间搜索可能为空，稍后自动就绪。`RebuildIndex()` 为按钮 / 对话框关闭 / 拖放三处共用入口。

### 8.7 手动添加扫描路径（v1.2）
- 搜索框旁「+ 路径」打开 `PathsDialog`：添加 exe（多选）/ 文件夹（递归）、删除路径、Esc 关闭。
- 也可从资源管理器**直接拖放** exe / 文件夹到窗口。
- `ScanPathsStore` 统一管理 `scanpaths.json`：兼容旧对象格式读取、规范化为字符串数组写入、增删即落盘、大小写不敏感去重；校验规则为「存在的 .exe 或存在的目录」。

### 8.8 窗口拖动与位置记忆（v1.2）
- 按住标题行 `DragMove()`，结束后写 `window.json`（`Left`/`Top`；`NaN` 时不写）。
- 恢复时校验「窗口上边缘中点是否落在任一屏幕内」，无效则回退默认定位。
- 底部**状态反馈条**（2.5 秒自动隐藏）用于拖放 / 添加的即时反馈。

### 8.9 失活自动隐藏优化（v1.3）
- **拖拽豁免**：轮询 `GetAsyncKeyState(VK_LBUTTON)`，左键按住期间绝不隐藏，松开后恢复 500ms 判定——解决"从资源管理器拖文件时窗口先消失"的问题。
- **保持窗口开关**：见 8.4。
- 拖放完成后窗口自动回到前台。

### 8.10 拼音（`Indexing/PinyinHelper.cs`）
基于 `PinYinConverterCore` 将中文名转为拼音首字母与全拼，使 `wx` → 微信、`jsq` → 计算器。

### 8.11 剪贴板历史
- **两种模式**：`App`（软件查找，默认）/ `Clip`（剪贴板历史）。呼出时默认 App 模式；窗口内按 **`Alt`** 切换（标题栏 Tab 高亮同步）。`Alt` 检测用 `Key.System` + 左/右 Alt 且排除 Ctrl/Shift，配 `_altDown` 防抖。
- **监听**：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE(0x031D)`，收到后 200 ms 防抖再读取，避免高频拷贝风暴。
- **捕获**：图片优先于文本；文本取 `UnicodeText`；读写异常（`ExternalException`）安全忽略。
- **搜索**：无关键词 → 按时间倒序全部；有关键词 → `子串` > `最长公共子串覆盖(≥0.3–0.45)` > `多词命中`。
- **图片**：缩略图 `DecodePixelWidth=40`；存储为 `images/{id}.png`。
- **容量上限**（`Clipboard/ClipboardStore.cs`）：`MaxTotalItems = 400`、`MaxImageItems = 60`、`MaxTextLength = 4000`。
- **右键菜单快捷键**（软件模式指向文件时有效）：`Ctrl+Shift+F` 打开文件所在位置、`Ctrl+Shift+C` 复制路径 / 文本、`Enter` 启动应用 / 将条目复制回剪贴板。

### 8.12 应用图标（v1.4）
- `Assets/app.ico` 为"单个放大镜"（荧光黄描边 + 深色圆角底），由 `tools/IconGen` 生成 7 档尺寸（16/24/32/48/64/128/256）并打包为 PNG 压缩 ICO。
- csproj 通过 `<ApplicationIcon>` 应用到 exe；任务栏 / Alt-Tab 与托盘保持一致。

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
| 热键无反应 | `Ctrl+Alt+Space` 被其他程序占用 → 看托盘气泡提示，双击托盘图标打开；或释放该快捷键。 |
| 搜不到某应用 | 少数 UWP 未暴露 `AppListEntry`，或应用未装到当前用户，属正常。 |
| 手动添加的路径没生效 | 检查 `scanpaths.json` 是否成功写入；路径必须指向存在的 exe 或目录（校验失败会静默拒绝）。 |
| 从资源管理器拖文件时窗口消失 | 已在 v1.3 修复（拖拽豁免）；若复现请检查 `GetAsyncKeyState` 轮询是否被改动。 |
| 启动瞬间搜索为空 | 索引后台构建约 1 秒，稍候自动就绪。 |
| 多实例无法启动 | 已有实例持有 `Local\SpotlightLauncher_Singleton`，关闭旧实例即可。 |
| 剪贴板模式不更新 | 确认系统剪贴板格式被监听（`WM_CLIPBOARDUPDATE`）；图片复制应出现缩略图。 |
| 取消自启后又自己开了 | 检查 `autostart.json` 的 `AutoStart` 是否为 false——该文件是防止静默恢复的偏好记录。 |

---

## 11. 版本与发布

- 当前版本：**v2.0.0**（`SpotlightLauncher.csproj` 的 `Version`）。
- 产物体积：自包含便携版 exe 约 152 MB；框架依赖版约 26 MB。
- 大文件（>100 MB）**不得**提交进 git；通过 **GitHub Releases** 分发。
- 提交源码时确保 `.gitignore` 已忽略 `bin/ obj/ dist/ dist-portable/ release/` 等构建产物。
