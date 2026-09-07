# WallpaperPure 维护手册

## 技术要点

- **显隐方案（v1.2 双通道）**：
  - 立即生效——定位桌面图标列表窗口 `SysListView32` 并 `ShowWindow(SW_HIDE/SW_SHOW)`，效果确定、无闪烁。定位路径：`Progman → SHELLDLL_DefView → SysListView32`；使用 Wallpaper Engine 等壁纸软件时 DefView 挂在 `WorkerW` 下，需 `EnumWindows` 遍历查找。
  - 重启持久——写注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\HideIcons`（DWORD），与系统右键菜单"查看 → 显示桌面图标"同一持久化位置。
  - 状态读取以图标窗口**实际可见性**为准（所见即所得），找不到窗口时回退注册表。
- **弃用方案记录（v1.1）**：曾用 `SHGetSetSettings + SSF_HIDEICONS`，因 `SHELLSTATE` 位域偏移与文档记忆不符（点击仅引起桌面闪烁、图标未隐藏），已弃用。教训：位域结构 interop 拿不准时不要硬猜，改走注册表/窗口直控这类可验证路径。
- **交互（v1.1）**：原地左键单击 = 切换图标显隐；按住左键拖动 = 自由移动按钮（超过系统拖动阈值判定）。拖动结束自动钳回屏幕可视区并保存位置。
- **按钮文案固定（v1.3）**：文本恒为"图标"，隐藏/显示状态仅靠颜色区分（灰=显示中，荧光黄 `#FFFAFC52`=已隐藏）。
- **切换过渡动画（v1.3）**：对图标窗口临时加 `WS_EX_LAYERED`，150ms 内 10 步 alpha 渐变（隐藏=淡出后 `SW_HIDE`；显示=`SW_SHOW` 后淡入），结束立即移除 layered 样式恢复原状。动画期间忽略连点；程序退出时若动画未完成会先落最终状态再退出（`FinishFadeOnExit`）。仅点击瞬间运行（10 次轻量调用），平时零开销；找不到图标窗口时自动回退为无动画直切。
- **开机自启（v1.4）**：托盘右键菜单"开机自启"勾选项，读写 `HKCU\...\Run\WallpaperPure`（值为带引号 exe 全路径，取 `Environment.ProcessPath`，单文件发布可用）。注册表为唯一状态源，无额外偏好文件；启动不自作主张改注册表。
- **位置持久化**：`SettingsStore` 读写 `%APPDATA%\WallpaperPure\settings.json`（WPF 逻辑坐标）。启动时优先恢复保存位置（校验仍在虚拟屏幕内，防分辨率变更后丢失），否则用默认物理像素坐标 `(2300, 1270)` 经 `TransformFromDevice` 转换。
- **Z 序（v1.1）**：窗口非 Topmost、`ShowActivated=False` + `Focusable=False`——按钮只贴桌面层，不遮挡用户已打开的窗口、不抢焦点。
- **无热键**：全部交互为左键单击/拖动 + 右键退出，托盘提供"开机自启 / 退出"。

## 目录结构

```
WallpaperPure/
  WallpaperPure.csproj
  app.manifest
  App.xaml / App.xaml.cs  # 托盘菜单：开机自启勾选 + 退出
  MainWindow.xaml / MainWindow.xaml.cs
  AutoStart.cs            # 开机自启（HKCU Run）
  SettingsStore.cs        # 位置持久化（%APPDATA%\WallpaperPure\settings.json）
  Native/
    NativeMethods.cs      # 图标窗口定位 + ShowWindow + 注册表 HideIcons + 分层动画原语
    TrayIcon.cs           # 无 WinForms 托盘
    TrayIconHelper.cs     # GDI 图标绘制
  RelayCommand.cs
```

## 构建命令

框架依赖版（`dist/`）：

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

绿色便携版（`dist-portable/`）：

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:SatelliteResourceLanguages=en \
  -o dist-portable
```

## 关键坑

1. **`SHELLSTATE` 位域 interop 不可靠**：`SHGetSetSettings + SSF_HIDEICONS` 实测点击只触发桌面闪烁、图标不隐藏（写入落到了错误位）。位域结构拿不准时改走注册表 + 窗口直控，行为可验证。
2. **壁纸软件改变桌面窗口层级**：Wallpaper Engine 等会把 `SHELLDLL_DefView` 从 `Progman` 挪到 `WorkerW` 下，定位图标窗口必须带 `EnumWindows` 兜底，否则找不到 `SysListView32`。
3. WPF `Left`/`Top` 是逻辑坐标；要把物理像素坐标放到正确位置，必须用 `TransformFromDevice` 转换。
4. 窗口无标题栏，初始位置要用 `Width/Height` 定尺寸（`ActualWidth` 在 Show 前为 0）。
5. 同文件若混用 `System.Drawing` 与 `System.Windows.Media`，Brush/Color 会歧义；GDI 全限定，WPF brush 用 `System.Windows.Media.Brush` 字段。
6. 点击/拖动二义性：`DragMove()` 会阻塞至松开并吞掉期间的 `MouseLeftButtonUp`；用 `_isDragging` 标志区分，Up 事件里仅未拖动时才切换。
7. 部署前须结束正在运行的实例（文件锁会让 `GenerateBundle` 报 "being used by another process"）；Git Bash 下 `taskkill //IM` 可能报"无效参数"，用 PowerShell `Stop-Process -Name WallpaperPure -Force`。
8. 过渡动画：对 Explorer 图标窗口临时加的 `WS_EX_LAYERED` 样式**必须在动画结束与程序退出两个路径都移除**，否则图标窗口会残留半透明状态；动画中的 alpha 已到 0 后还需 `SW_HIDE`（alpha=0 的窗口仍参与布局/命中）。
9. `ShowWindow` 若同时需要 P/Invoke 的 `(IntPtr,int)` 与对外 `(IntPtr,bool)` 重载，P/Invoke 版保持 private，否则外部调用会命中 int 版导致 CS0122。
10. **淡入失效坑（v1.3→v1.4 修复）**：显示路径必须先 `SW_SHOW` 再 `EnableLayered(alpha=0)` 逐帧升 alpha；反过来（先加 layered 置透明再 SW_SHOW）窗口以全透明快照显示，后续 alpha 更新不生效，表现为"无淡入、结束瞬间突然变亮"。淡出（窗口本已可见时加 layered）无此问题。
