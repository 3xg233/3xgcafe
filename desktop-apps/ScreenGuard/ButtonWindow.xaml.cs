using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScreenGuard;

/// <summary>
/// 桌面悬浮胶囊按钮（与 WallpaperPure 同款尺寸与拖动交互）：
/// 左键单击 = 立即锁定；右键 = 弹出操控菜单（设置 / 临时关闭监控等）；
/// 按住拖动 = 自由摆放，位置记忆。
/// 非 Topmost、不抢焦点——被打开的窗口正常盖住。
/// </summary>
public partial class ButtonWindow : Window
{
    // 默认位置（物理像素）：与 WallpaperPure 默认位（2300,1270）并排放在左侧
    private const double DefaultPhysicalLeft = 2140;
    private const double DefaultPhysicalTop = 1270;

    private Point _dragStart;
    private bool _isDragging;

    /// <summary>请求立即锁定。</summary>
    public event Action? LockRequested;

    /// <summary>请求打开操控菜单。由 App 提供实时构建的菜单（含"临时关闭监控"等最新状态）。</summary>
    public event Action<ButtonWindow>? MenuRequested;

    public ButtonWindow()
    {
        InitializeComponent();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        var transform = source.CompositionTarget!.TransformFromDevice;

        var saved = ButtonSettings.Load();
        if (saved is { } pos && IsPositionOnScreen(pos.Left, pos.Top))
        {
            Left = pos.Left;
            Top = pos.Top;
        }
        else
        {
            var logical = transform.Transform(new Point(DefaultPhysicalLeft, DefaultPhysicalTop));
            Left = logical.X;
            Top = logical.Y;
        }
    }

    // ---------- 鼠标交互：原地单击 = 菜单；按住拖动 = 移动位置 ----------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _isDragging = false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
            return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(p.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _isDragging = true;
            try
            {
                DragMove(); // 阻塞至鼠标松开
            }
            catch (InvalidOperationException)
            {
                // 拖动尚未真正开始时松开等罕见时序，忽略
            }
            _isDragging = false;
            ClampToScreen();
            SavePosition();
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            LockRequested?.Invoke();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
        => MenuRequested?.Invoke(this);

    /// <summary>在按钮旁弹出菜单（由 App 传入实时构建的菜单）。</summary>
    public void OpenMenu(ContextMenu menu)
    {
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    /// <summary>菜单里的"立即锁定"与左键单击共用同一入口。</summary>
    public void RaiseLockRequested() => LockRequested?.Invoke();

    // ---------- 位置持久化 ----------

    private void SavePosition() => ButtonSettings.Save(new ButtonSettings.Pos(Left, Top));

    protected override void OnClosed(EventArgs e)
    {
        SavePosition();
        base.OnClosed(e);
    }

    /// <summary>拖动结束时把窗口拉回可视区域，防止整个按钮跑出屏幕。</summary>
    private void ClampToScreen()
    {
        double vx = SystemParameters.VirtualScreenLeft;
        double vy = SystemParameters.VirtualScreenTop;
        Left = Math.Clamp(Left, vx, vx + SystemParameters.VirtualScreenWidth - ActualWidth);
        Top = Math.Clamp(Top, vy, vy + SystemParameters.VirtualScreenHeight - ActualHeight);
    }

    /// <summary>恢复保存的位置前校验仍在屏幕内（防止改分辨率后按钮消失）。</summary>
    private static bool IsPositionOnScreen(double left, double top)
    {
        double vx = SystemParameters.VirtualScreenLeft;
        double vy = SystemParameters.VirtualScreenTop;
        return left >= vx && top >= vy &&
               left < vx + SystemParameters.VirtualScreenWidth &&
               top < vy + SystemParameters.VirtualScreenHeight;
    }
}
