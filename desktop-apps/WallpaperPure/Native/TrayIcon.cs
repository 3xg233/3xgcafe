using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace WallpaperPure.Native;

/// <summary>
/// Win32 Shell_NotifyIconW 托盘图标封装（替代 WinForms NotifyIcon）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAY_CB = WM_USER + 1;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000003;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private HwndSource? _source;
    private NOTIFYICONDATAW _nid;
    private bool _added;
    private IntPtr _hicon;

    public ContextMenu? ContextMenu { get; set; }
    public event Action? DoubleClick;

    public TrayIcon(string tip, IntPtr hicon)
    {
        _hicon = hicon;
        _source = new HwndSource(new HwndSourceParameters
        {
            Width = 0, Height = 0, PositionX = 0, PositionY = 0,
            WindowStyle = 0, ExtendedWindowStyle = 0,
        });
        _source.AddHook(WndProc);

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CB,
            hIcon = hicon,
            szTip = tip ?? string.Empty,
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY_CB)
        {
            uint mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WM_LBUTTONDBLCLK)
            {
                DoubleClick?.Invoke();
            }
            else if (mouse == WM_RBUTTONUP)
            {
                SetForegroundWindow(hwnd);
                if (ContextMenu != null)
                {
                    ContextMenu.Placement = PlacementMode.MousePoint;
                    ContextMenu.IsOpen = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            if (_added) { Shell_NotifyIconW(NIM_DELETE, ref _nid); _added = false; }
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
        if (_hicon != IntPtr.Zero) { DestroyIcon(_hicon); _hicon = IntPtr.Zero; }
    }
}
