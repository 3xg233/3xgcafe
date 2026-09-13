# 3xgcafe Guard 维护手册

> 3xgcafe 系列工具之一 · 程序集名 `3xgcafe-Guard`（原名 ScreenGuard）

面向后续维护/AI 复现的技术说明。用户视角请看 [README.md](README.md)。

## 定位

空闲自动锁定工具：系统级检测无操作时长 → 全屏锁屏窗（覆盖虚拟桌面、置顶、禁止关闭）→ 全局低级输入钩子封锁键鼠 → 输入正确密码解锁。

**不碰系统电源策略**（不关机、不休眠、不注销），后台程序正常运行。

## 技术要点

- **空闲检测**：轮询 `user32!GetLastInputInfo`（1 秒一次），得到系统级最后输入时刻。
  `uint elapsed = (uint)Environment.TickCount - info.dwTime;` —— 用 uint 运算天然处理 49 天回绕。
  该 API 只统计当前会话，不区分哪个进程产生输入，因此"用户自己在打字"也会正确重置计时。
- **输入封锁**：`SetWindowsHookEx(WH_KEYBOARD_LL / WH_MOUSE_LL)` 全局低级钩子，回调返回非零即吞掉事件。
  钩子必须装在**有消息泵的线程**上——本程序装在 WPF UI 线程（Dispatcher 有消息循环），无需额外线程。
  回调必须极快，故逻辑只做"前台窗口是否本进程 + 按键是否在黑名单"两件事。
- **放行策略**：锁屏窗自身需要接收密码输入，所以判断 `GetForegroundWindow()` 的进程 ID：
  是本进程 → 放行普通按键，仅拦 `VK_LWIN/VK_RWIN/VK_TAB/VK_ESCAPE/VK_F4`（这四类配合 Alt/Ctrl 可切窗口、开开始菜单、关窗口）；
  非本进程 → 键盘鼠标全部吞掉。
  注意 `VK_ESCAPE` 必须拦：`Alt+Esc` 会把锁屏窗最小化，直接绕过锁定。
- **前台抢占**：`SetForegroundWindow` 受前台锁限制，直接调用常静默失败。
  必须 `AttachThreadInput(前台线程, 本线程, true)` → `SetForegroundWindow` → 解绑，见 `NativeMethods.ForceForeground`。
  250ms 看护定时器持续抢占 + `SetWindowPos(HWND_TOPMOST)`，防止被其他置顶窗口（如 MiniMonitor）盖住。
- **铺满虚拟桌面**：`GetSystemMetrics(SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN/SM_CXVIRTUALSCREEN/SM_CYVIRTUALSCREEN)`
  取物理像素，除以 `VisualTreeHelper.GetDpi(this)` 的缩放比转成 WPF 逻辑坐标赋给 `Left/Top/Width/Height`。
  密码面板用 Canvas 定位，居中于**主显示器**（虚拟桌面可能横跨多屏，居中到整体会偏）。
- **锁屏背景（v1.2 改为 DWM 实时模糊）**：锁屏窗 `AllowsTransparency=True` + 透明背景，
  `AcrylicBlur.Enable` 调 `SetWindowCompositionAttribute`（`ACCENT_ENABLE_ACRYLICBLURBEHIND`，
  undocumented 但 Win10 1803+/Win11 稳定）对窗口**后方内容**实时高斯模糊 + 染色
  `0xB0141416`（深色 69% 不透明，API 侧需转 ABGR 字节序）——后方程序运行状态实时透出。
  `LockBackground.CreateBlurred()` 快照（降采样 1/12 → 盒式模糊 → 压暗 → 白雾）**降级为兜底**：
  仍在锁屏前抓取（防拍到锁屏自身），仅当亚克力应用失败（远程桌面/旧系统）时显示；再失败用纯色 `#0A0A0C`。
  设置里的"锁屏背景实时模糊"关闭则完全回退纯深色背景。
- **密码存储**：PBKDF2-SHA256，12 万次迭代，16 字节随机盐，32 字节派生密钥，Base64 存 JSON；
  比对用 `CryptographicOperations.FixedTimeEquals` 防时序侧信道。**不存明文**。
- **防暴力**：连错 3 次开始冷却，时长 `min(5 × (failCount - 2), 60)` 秒，冷却期间输入框与按钮禁用。
- **窗口关闭防护**：`Closing` 事件中未解锁一律 `e.Cancel = true`；`Alt+F4` 已被钩子拦截，这是第二层。
- **自启**：`AutoStart.cs` 写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\3xgcafe-Guard`，
  值取 `Environment.ProcessPath`（即当前运行的 exe），注册表是唯一状态源。
- **单实例**：`Mutex(@"Local\3xgcafe-Guard_SingleInstance")`。
- **桌面按钮（v1.3，v1.3.1 修订交互）**：`ButtonWindow` 非 Topmost + `ShowActivated=False` + `Focusable=False`（不抢焦点、被窗口正常盖住），
  尺寸与 WallpaperPure 完全一致（80×26，圆角 13，字号 12），无图标纯文字"锁定"。
  **左键单击 = `LockRequested` → `LockNow`**（拖动超阈值不算点击，与 WallpaperPure 同款阈值判定）；
  **右键 = `MenuRequested` → `App.ShowMainMenu`** **实时** `BuildMenu()` 后在鼠标处弹出（每弹一次重建，
  保证"开机自启"勾选态与监控暂停状态不过期；托盘菜单同一方法，两处共用）。
  位置存 `button.json`，启动校验在虚拟屏内否则回默认物理坐标 (2140,1270)——与 WallpaperPure 默认位
  (2300,1270) 并排、其左侧。
  **注意**：按钮属本进程，锁定期间 `IsOwnWindowForeground` 会把它视为"自己"——但它 `Focusable=False` 且被
  锁屏窗盖住，点不到，不构成绕过路径；不要给按钮加 Topmost。
- **可选保持屏幕常亮**：`SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)`，
  解锁时用 `SetThreadExecutionState(ES_CONTINUOUS)` 还原。默认关闭，避免干扰系统电源策略。

## 已知限制

1. **`Ctrl+Alt+Del` 无法拦截**：SAS 由 winlogon 在安全桌面处理，用户态钩子不生效。
   这既是设计限制也是忘记密码时的唯一逃生通道（任务管理器结束进程 → 删配置里密码字段 → 重启）。
   文档已向用户说明；若将来要加固，可考虑"看门狗进程 + 主进程被结束后立即重启锁定"，但会与合法退出流程冲突，需谨慎设计。
2. 独占全屏的 3D 游戏在锁定瞬间可能不立即让出前台，锁屏窗会以非独占方式显示（游戏会被强制切到后台）。
3. 锁屏窗是**不透明**覆盖（背景图 + 不透明底），不使用分层透明窗口——远程桌面/部分驱动下分层窗口渲染不可靠（沿用本仓库其他工具的经验）。
4. 背景是**静态快照**，锁定期间不会随底层画面变化（例如视频仍在播放，锁屏背景不会动）。
   这与 Windows 自带锁屏的观感一致；若将来要动态背景，需改用实时模糊 API 并接受上述兼容性风险。
5. 锁屏背景会"隐约可见"用户锁屏前的画面轮廓（这也是需求本身）。若用户对某些内容特别敏感，
   在设置里关闭"锁屏背景模糊"即可回退成纯深色。

## 目录结构

```
ScreenGuard/
  ScreenGuard.csproj
  app.manifest
  nuget.config
  App.xaml / App.xaml.cs        # 托盘、锁定/解锁编排、暂停监控、首次设置引导
  ButtonWindow.xaml / .cs       # 桌面悬浮胶囊按钮（单击/右键弹主菜单，拖动摆位）
  ButtonSettings.cs             # 按钮位置持久化（%APPDATA%\3xgcafe\Guard\button.json）
  LockWindow.xaml / .cs         # 全屏锁屏窗口（密码校验、冷却、看护定时器）
  LockBackground.cs             # 桌面抓屏 → 模糊压暗 → 毛玻璃背景
  SettingsWindow.xaml / .cs     # 设置（锁定时间/密码/提示语/自启）
  IdleMonitor.cs                # 空闲计时 + 暂停/恢复
  AppSettings.cs                # %APPDATA%\3xgcafe\Guard\settings.json
  PasswordHasher.cs             # PBKDF2-SHA256
  AutoStart.cs                  # HKCU\...\Run\3xgcafe-Guard
  RelayCommand.cs
  Native/
    NativeMethods.cs            # GetLastInputInfo / 钩子 / 前台抢占 / 屏幕度量 / 电源状态 / 深色标题栏
    InputBlocker.cs             # 键盘鼠标低级钩子封装
    TrayIcon.cs                 # 无 WinForms 托盘（Shell_NotifyIconW）
    TrayIconHelper.cs           # GDI 绘制锁形托盘图标
```

## 构建

```bash
# 本机 SDK 在 ~/.dotnet；环境变量必须先补齐，否则 NuGet restore 报 path1 null
export PATH="$HOME/.dotnet:$PATH" APPDATA="C:\Users\qq318\AppData\Roaming" \
       LOCALAPPDATA="C:\Users\qq318\AppData\Local" USERPROFILE="C:\Users\qq318" \
       ProgramFiles="C:\Program Files" TEMP="C:\Users\qq318\AppData\Local\Temp" TMP="C:\Users\qq318\AppData\Local\Temp"

cd ScreenGuard

# 绿色版（单文件自包含，日常用）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true -p:SatelliteResourceLanguages=en -o dist-portable

# 开发版（framework-dependent，体积小、启动快，仅调试用）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

发布前若程序正在运行，先结束进程（文件锁会让 GenerateBundle 失败）：
`Stop-Process -Name 3xgcafe-Guard -Force`（PowerShell）。Git Bash 下 `taskkill //IM` 会报"无效参数"。

绿色版统一覆盖到 `C:\Users\qq318\Desktop\3xgcafe\`。

## 命名与隔离（v2.0）

3xgcafe 系列统一命名后，本工具的对外标识与运行环境标识：

| 项目 | 取值 |
|------|------|
| 显示名（窗口标题 / 托盘 / 文档） | `3xgcafe Guard` |
| 程序集 / exe 名 | `3xgcafe-Guard`（`3xgcafe-Guard.exe`） |
| 注册表自启值名 | `3xgcafe-Guard`（旧 `ScreenGuard` 自动清理） |
| 单实例互斥名 | `Local\3xgcafe-Guard_SingleInstance` |
| 数据目录 | `%APPDATA%\3xgcafe\Guard\`（settings.json / button.json） |
| IPC 通道名 | `3xgcafe-Guard`（旧 `3xgcafe-ScreenGuard`） |
| 工程目录 / 命名空间 | 保持 `ScreenGuard`（仅内部标识，不改） |

> 这套隔离确保新版与旧版能在同一台机器共存而不互相干扰：进程名、互斥量、自启项、数据目录、IPC 通道均不复用。
> ⚠️ 数据目录变更后，**旧版设置的锁定密码不会自动迁移** —— 首次运行新版需重新设置密码，或手动把 `%APPDATA%\ScreenGuard\settings.json` 复制到新目录。


## 关键坑

1. **钩子需要消息泵**：低级钩子的回调在被安装线程上执行，必须装在有消息循环的线程（WPF UI 线程即可）。
   若装在线程池线程上，钩子会立即失效或永远不被调用。
2. **必须持有委托引用**：`SetWindowsHookEx` 的委托若被 GC 回收，回调会崩溃。`InputBlocker` 用字段保存 `_keyboardProc` / `_mouseProc`。
3. **必须 `UnhookWindowsHookEx`**：解锁/退出时务必解绑，否则会一直吞输入。
   `App.OnExit` 与 `InputBlocker.Dispose` 双重兜底。
4. **`VK_ESCAPE` 不能放行**：`Alt+Esc` 会最小化锁屏窗并露出桌面，是真实存在的绕过路径。
5. **不要用 `BlockInput`**：该 API 需要管理员权限，且一旦进程崩溃会永久卡死键鼠（直到重启），风险远大于收益。
6. **实时模糊必须 `AllowsTransparency=True`**（v1.2 起锁屏窗默认如此）：亚克力透出的是窗口后方的 DWM 合成内容，
   窗口必须透明。远程桌面等亚克力不可用的场景 `AcrylicBlur.Enable` 返回 false，代码自动回退快照/纯色；
   不要在亚克力生效路径上再铺不透明底（会把模糊效果盖掉）。
7. **多屏铺满用虚拟屏矩形**，但密码面板要居中于主屏，否则双屏用户会觉得面板跑到屏幕缝里。
8. **可访问性**：`AppSettings` / `InputBlocker` 若声明为 `internal`，而 `LockWindow`/`SettingsWindow`（XAML 生成的 partial 类为 `public`）构造函数暴露它们，会报 CS0051；三者的可访问性必须一致为 `public`。
9. **`JsonIgnore` 需要 `using System.Text.Json.Serialization;`**（与 `System.Text.Json` 不是同一命名空间）。
10. WPF 深色界面：默认可控样式是浅色的，输入框/按钮都要自定义 `ControlTemplate`（见 `App.xaml` 资源），
    否则深色背景上的默认按钮 hover 会变成系统高亮色、文字不可读。标题栏深色用
    `DwmSetWindowAttribute(hwnd, 20, ...)`（Win11）/ `19`（Win10 1809+），失败时无副作用。
11. **GDI 抓屏结果没有 alpha**：`Graphics.CopyFromScreen` 落到的 32bpp 位图，alpha 字节全为 0；
    直接转成 WPF `BitmapSource` 会渲染成**全透明**（看起来像背景没生效）。
    必须强制把每个像素的第 4 字节置 255（`LockBackground.ToImageSource` 里的循环），再 `WritePixels`。
12. **快照必须在锁屏窗 `Show()` 之前抓**，否则会把锁屏界面自己拍进去。
13. **同一文件里不要同时 `using System.Drawing` 与 `System.Windows.Media`**：`Color`/`Size`/`PixelFormat` 会歧义。
    `LockBackground.cs` 只 using Drawing 系列，WPF 类型（`ImageSource`/`WriteableBitmap`/`Int32Rect`/`PixelFormats`）全部写全名。
