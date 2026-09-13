using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenGuard;

/// <summary>
/// 开机自启管理：HKCU\...\Run\3xgcafe-Guard，指向当前运行的 exe
/// （注册表是唯一状态源，无额外偏好文件；托盘菜单勾选即同步）。
/// 旧版本的值名 ScreenGuard 会在每次读写时清理，避免新旧版本同时自启而冲突。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "3xgcafe-Guard";

    /// <summary>旧版本使用的注册表值名，仅用于清理。</summary>
    private const string LegacyValueName = "ScreenGuard";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        // 清理旧版本自启项：新版接管后不应再有第二份指向旧 exe 的启动项
        key.DeleteValue(LegacyValueName, false);

        if (enable)
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, "\"" + exe + "\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
