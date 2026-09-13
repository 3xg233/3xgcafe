# 3xgcafe Console 维护手册

> 面向后续维护者（含 AI 助手）。用户说明见 [README.md](README.md)。

## 定位

第 5 个桌面工具：**统一管理面板**。用「进程级托管 + 命名管道状态通道」两层机制管理其余四个工具，
不改变各工具相互独立的前提（不共享代码、不互相引用）。

## 架构

```
3xgcafeConsole/
  App.xaml / App.xaml.cs        # 单实例 + 工业风控件样式（ConsoleButton / ConsoleAccentButton）
  MainWindow.xaml / .cs         # 无边框自绘标题栏 + 卡片宿主 WrapPanel + 底栏
  AddToolWindow.xaml / .cs      # 添加工具对话框（返回 ToolDefinition）
  ToolDefinition.cs             # 工具定义模型 + tools.json 读写 + 默认配置
  PipeClient.cs                 # 命名管道客户端（一条请求一条响应）
  app.manifest / nuget.config / README.md / MAINTENANCE.md
```

### 两层机制

1. **进程级**：`Process.GetProcessesByName(<exe 名>)` 判断存活；`Process.Start` 启动；
   停止时先发 `quit` 指令，900ms 后仍在则 `Kill()`。
   附带信息：PID、`WorkingSet64`（内存 MB）、`StartTime`（已运行时长）。
2. **状态通道**：命名管道 `3xgcafe-<工具Id>`，一行 JSON 请求 → 一行 JSON 响应，**连接用完即断**。

### 协议（ver 1）

请求：`{"cmd":"status"}` / `{"cmd":"pause","minutes":30}`

响应：
```json
{ "ok": true, "state": "running", "detail": "保护中，3 分 41 秒 后锁定",
  "ver": 1, "data": { "paused": "0", "locked": "0" } }
```

- `state` 约定值：`running` / `paused` / `locked` / `stopping` / `error`
  （面板只对 `locked` 做特殊处理——停止需二次确认；`paused` 显示为黄色）
- `data` 为可选的字符串键值对，供将来扩展（面板当前用于自启态等）
- 工具侧实现见各工具 `Ipc/ToolIpcServer.cs`（同源文件复制，仅命名空间不同）

### 各工具已实现的指令

| 工具 | 状态 detail | 指令 |
|---|---|---|
| MiniMonitor | `CPU x% · 内存 y% · z°C` | `show`、`toggle_topmost`、`quit` |
| SpotlightLauncher | `索引 N 项 · 剪贴板 M 条` | `show`、`quit` |
| WallpaperPure | `桌面图标：显示中/已隐藏` | `toggle_icons`、`show_icons`、`hide_icons`、`quit` |
| ScreenGuard | 保护中倒计时 / 已暂停 / 已锁定 | `lock`、`pause`(minutes)、`resume`、`quit` |

## 给新工具接入通道（约 60 行）

1. 复制任一工具的 `Ipc/ToolIpcServer.cs` 到新工具，改命名空间。
2. 在 `App.OnStartup` 末尾：

```csharp
_ipc = new ToolIpcServer("MyTool", HandleIpc);   // 通道名自动 = 3xgcafe-MyTool

private string HandleIpc(string cmd, IReadOnlyDictionary<string, string> args)
{
    switch (cmd)
    {
        case "status":
            return ToolIpc.Response(true, "running", "自定义状态文字",
                new Dictionary<string, string> { ["字段"] = "值" });
        case "do_it":
            Dispatcher.BeginInvoke(new Action(() => 做点什么()));
            return ToolIpc.Response(true, "running", "已执行", null);
        case "quit":
            Dispatcher.BeginInvoke(new Action(ExitApp));
            return ToolIpc.Response(true, "stopping", "正在退出", null);
        default:
            return ToolIpc.Response(false, "error", "未知命令: " + cmd, null);
    }
}
```

3. `App.OnExit` 里 `_ipc?.Dispose();`
4. 面板 `tools.json` 加一条（或界面「添加工具」），`pipeName` = `3xgcafe-MyTool`。

## 关键坑

0. **没有 `StartupUri` 就必须在 `OnStartup` 里手动 `new MainWindow()`**：
   本项目的 `App.xaml` 只声明了 `ShutdownMode="OnMainWindowClose"`，没有 `StartupUri`。
   若忘记创建窗口，进程会正常启动、常驻，但**界面上什么都不出现**（看起来像"启动了没反应"，实为无窗口可显示）。
   四个小工具也是同样的显式创建模式，保持一致。
1. **管道处理在后台线程**：涉及 WPF 控件的操作必须 `Dispatcher.BeginInvoke`（各工具已按此写）。
2. **不要用长连接**：面板轮询频率高（1.5s × N 工具），长连接会带来状态机与超时清理的复杂度；
   一请求一连接最省心，管道不再存在（工具退出）时直接 `catch` 返回 null。
3. **超时必须给**：`ConnectAsync` 与读取响应都要限时，否则工具卡住会拖慢整个轮询周期
   （面板：连接 700ms / 指令 1500ms）。
4. **托盘类工具没有主窗口**：`CloseMainWindow()` 无效，停止只能靠通道 `quit` 或 `Kill()`
   （这也是一开始就设计通道的原因）。
5. **ScreenGuard 锁定状态是危险的停止目标**：锁定中被杀 = 直接解锁。面板必须在
   `state == "locked"` 时禁止一键停止（当前为二次确认）。改动此处需格外谨慎。
6. **静态资源用 `FindResource`**：卡片是代码构建的，样式需 `(Style)FindResource("ConsoleButton")`。
7. **`Polygon` 切角**：卡片右上角"缺角"是用背景色三角覆盖实现的（`Fill=BgBrush`），
   不是几何裁切——改卡片背景色时必须同步改这个填充色。
8. **注册表自启值名**：默认取 `id`（与各工具自己写入的值名一致），否则会出现"面板显示未自启、
   工具自己却已自启"的分裂状态；换 `RunValueName` 时确认与工具一致。
9. **编译**：本机需先 `export APPDATA / ProgramFiles / TEMP / TMP` 等环境变量再 `dotnet publish`
   （否则 restore 报 `path1` 为空），两套产物沿用仓库统一命令。

## 命名与隔离（v2.0）

3xgcafe 系列统一命名后，本工具的对外标识与运行环境标识：

| 项目 | 取值 |
|------|------|
| 显示名 | `3xgcafe Console` |
| 程序集 / exe 名 | `3xgcafe-Console`（`3xgcafe-Console.exe`） |
| 注册表自启值名 | `3xgcafe-Console` |
| 单实例互斥名 | `Local\3xgcafe-Console_SingleInstance` |
| 配置目录 | `%APPDATA%\3xgcafe\Console\tools.json` |
| 便携版默认目录 | `桌面\3xgcafe\Current version\便携版\` |
| 工程目录 / 命名空间 | 保持 `3xgcafeConsole` / `ThreeXGCafeConsole`（仅内部标识，不改） |

> 工具清单里的 `exePath` / `pipeName` / `runValueName` 均按 `3xgcafe-<工具Id>` 约定生成（如 Id 为 `Monitor` → `3xgcafe-Monitor.exe` / `3xgcafe-Monitor`）。
> ⚠️ 旧的 `%APPDATA%\3xgcafeConsole\tools.json` **不会自动迁移**；新版首次运行会在新目录重建默认清单。若曾自定义过工具条目，请手动复制过去。


## 交付

- 绿色版 `dist-portable/3xgcafe-Console.exe` → 同步到 `桌面\3xgcafe\Current version\便携版\`
- 开发版 `dist/`（framework-dependent，附 DEV_ONLY.txt）
