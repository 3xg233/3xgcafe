using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WallpaperPure;

/// <summary>
/// 开机自启管理：HKCU\...\Run\WallpaperPure，指向当前运行的 exe
/// （注册表是唯一状态源，无额外偏好文件；托盘菜单勾选即同步）。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WallpaperPure";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
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
