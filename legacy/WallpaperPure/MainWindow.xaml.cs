using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperPure.Native;

namespace WallpaperPure;

public partial class MainWindow : Window
{
    // 默认位置（物理像素）：首次启动时置于右下角壁纸 INFO 文本上方。
    // 之后以用户拖动后保存的位置（逻辑坐标）为准。
    private const double DefaultPhysicalLeft = 2300;
    private const double DefaultPhysicalTop = 1270;

    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0x52));

    // 切换过渡动画：150ms / 10 步，仅点击瞬间运行，平时零开销
    private const int FadeSteps = 10;
    private const int FadeIntervalMs = 15;

    private Point _dragStart;
    private bool _isDragging;

    private DispatcherTimer? _fadeTimer;
    private IntPtr _fadeTarget;
    private int _fadeStep;
    private bool _fadeTargetVisible;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        var transform = source.CompositionTarget!.TransformFromDevice;

        var saved = SettingsStore.Load();
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
        UpdateAppearance();
    }

    // ---------- 鼠标交互：原地单击 = 切换显隐；按住拖动 = 移动位置 ----------

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
            ToggleIcons();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "退出",
                    Command = new RelayCommand(() => Application.Current.Shutdown())
                }
            }
        };
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ---------- 桌面图标显隐（带淡入淡出过渡） ----------

    private void ToggleIcons()
    {
        if (_fadeTimer != null)
            return; // 动画进行中忽略连点

        bool targetVisible = !NativeMethods.AreDesktopIconsVisible();
        SetAppearance(targetVisible); // 按钮颜色立即反映目标状态

        IntPtr listView = NativeMethods.FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            // 找不到图标窗口（罕见）：退回无动画直接设置
            NativeMethods.SetDesktopIconsVisible(targetVisible);
            UpdateAppearance();
            return;
        }

        StartFade(listView, targetVisible);
    }

    private void StartFade(IntPtr listView, bool targetVisible)
    {
        _fadeTarget = listView;
        _fadeTargetVisible = targetVisible;
        _fadeStep = 0;

        if (targetVisible)
        {
            // 淡入：先恢复显示，再于同一帧内加分层并置透明（肉眼无闪），随后 alpha 逐帧上升。
            // 注意顺序——若先加分层设 alpha=0 再 SW_SHOW，窗口会以全透明快照显示，
            // 后续 alpha 更新不生效，表现为"没有淡入、结束瞬间突然变亮"。
            NativeMethods.ShowWindow(listView, show: true);
            NativeMethods.EnableLayered(listView, 0);
        }
        else
        {
            // 淡出：窗口可见时加分层保持全亮，随后 alpha 逐帧下降，动画结束才真正隐藏
            NativeMethods.EnableLayered(listView, 255);
        }

        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(FadeIntervalMs)
        };
        _fadeTimer.Tick += OnFadeTick;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        _fadeStep++;
        double t = _fadeStep / (double)FadeSteps;
        byte alpha = _fadeTargetVisible ? (byte)(255 * t) : (byte)(255 * (1 - t));
        NativeMethods.SetAlpha(_fadeTarget, alpha);

        if (_fadeStep < FadeSteps)
            return;

        // 动画完成：应用最终状态并还原窗口样式
        _fadeTimer!.Stop();
        _fadeTimer.Tick -= OnFadeTick;
        _fadeTimer = null;

        NativeMethods.SetDesktopIconsVisible(_fadeTargetVisible);
        NativeMethods.DisableLayered(_fadeTarget);
        UpdateAppearance();
    }

    /// <summary>若退出时动画仍在进行，立即完成切换，避免图标停在半透明状态。</summary>
    private void FinishFadeOnExit()
    {
        if (_fadeTimer == null)
            return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= OnFadeTick;
        _fadeTimer = null;
        NativeMethods.SetDesktopIconsVisible(_fadeTargetVisible);
        NativeMethods.DisableLayered(_fadeTarget);
    }

    private void UpdateAppearance()
        => SetAppearance(NativeMethods.AreDesktopIconsVisible());

    private void SetAppearance(bool iconsVisible)
        => RootBorder.Background = iconsVisible ? GrayBrush : YellowBrush;

    // ---------- 位置持久化 ----------

    private void SavePosition() => SettingsStore.Save(new SettingsStore.WindowPos(Left, Top));

    protected override void OnClosed(EventArgs e)
    {
        FinishFadeOnExit();
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
