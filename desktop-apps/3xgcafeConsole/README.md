# 3xgcafe Console

小工具的统一管理面板：**一个界面看状态、启停全部工具**，风格参照工业科幻界面（深炭底 + 工业黄）。

## 能做什么

| 能力 | 说明 |
|---|---|
| 状态总览 | 每个工具一张卡片：运行中 / 已停止 / 已锁定 / 已暂停，顶栏显示 `0X / 0X online` |
| 启停控制 | 每张卡片可 **启动 / 停止 / 重启**；底部可 **全部启动 / 全部停止** |
| 实时状态 | 通过状态通道读取真实业务信息（MiniMonitor 的 CPU/内存/温度、ScreenGuard 的倒计时与暂停状态、SpotlightLauncher 的索引与剪贴板条数、WallpaperPure 的图标显隐） |
| 快捷指令 | 卡片上直接下发工具指令：立即锁定、暂停监控、切换桌面图标、唤起窗口/置顶等 |
| 开机自启 | 每张卡片一个「自启」勾选（写 `HKCU\...\Run`），底部可勾选「面板随 Windows 启动」 |
| 扩展 | 底部「添加工具」可把任意 exe 纳入管理，无需改代码 |

## 用法

1. 双击 `3xgcafe-Console.exe`（与其它工具放在同一目录 `桌面\3xgcafe\Current version\便携版\`）。
2. 卡片默认按 `MiniMonitor / SpotlightLauncher / WallpaperPure / ScreenGuard` 排布，状态每 1.5 秒刷新一次。
3. 首次运行会自动生成配置：`%APPDATA%\3xgcafe\Console\tools.json`。

## 状态灯含义

| 颜色 | 含义 |
|---|---|
| 绿 | 进程在运行，且状态通道正常 |
| 黄 | 进程在运行，但**该工具没有通道**（老版本）或状态为「已暂停 / 已锁定」 |
| 灰 | 进程未运行 |

## 添加自己的工具

点底部 **添加工具**，填写名称与 exe 路径即可。若该工具实现了状态通道（见下），把通道名填成 `3xgcafe-<工具Id>`，面板会自动显示其状态与指令按钮；没有通道也能做启动/停止/自启管理。

> 若工具自带设置界面（实现了 `settings` 指令），卡片上会出现「设置」按钮，可直接从面板打开它。

也可以直接编辑 `%APPDATA%\3xgcafe\Console\tools.json`：

```json
{
  "tools": [
    {
      "id": "MyTool",
      "name": "我的工具",
      "subtitle": "一句话说明",
      "exePath": "C:\\Users\\你\\Desktop\\3xgcafe\\MyTool.exe",
      "pipeName": "3xgcafe-MyTool",
      "runValueName": "MyTool",
      "builtIn": false,
      "quickActions": [
        { "label": "做点什么", "cmd": "do_it", "args": { "minutes": "30" } }
      ]
    }
  ]
}
```

## 安全提示

- **ScreenGuard 处于「已锁定」状态时，停止/重启会弹二次确认**：强行停止会让锁屏立即失效（等于绕过本机防护）。
- 「全部停止」若涉及已锁定的 ScreenGuard 同样会先确认。
- 面板不申请管理员权限，只操作当前用户的进程与注册表自启项。

## 说明

- 停止工具优先走**通道优雅退出**；工具没有通道或没响应时，才结束进程。
- 面板自身不含任何联网行为。
