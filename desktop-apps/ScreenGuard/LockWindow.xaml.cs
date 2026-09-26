using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenGuard.Native;

namespace ScreenGuard;

/// <summary>
/// 全屏锁屏窗口：覆盖整个虚拟桌面（含多显示器），置顶、禁止关闭，
/// 期间由 InputBlocker 封锁键盘鼠标，输入正确密码后解锁。
/// </summary>
public partial class LockWindow : Window
{
    private readonly AppSettings _settings;
    private readonly InputBlocker _blocker;
    private readonly DispatcherTimer _guardTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _cooldownTimer;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _unlocked;
    private bool _displayKeptOn;
    private int _failCount;
    private int _cooldownSeconds;
    private bool _reveal;

    /// <summary>桌面模糊快照，仅作实时模糊失败时的兜底。</summary>
    private readonly ImageSource? _backdrop;

    /// <summary>实时模糊染色（深色，约 69% 不透明度，保证隐私同时透出后方轮廓）。</summary>
    private const uint AcrylicTintArgb = 0xB0141416;

    private static readonly SolidColorBrush SolidBackdropBrush =
        new(System.Windows.Media.Color.FromRgb(0x0A, 0x0A, 0x0C));

    /// <summary>解锁成功时触发（由 App 负责解除输入封锁并关闭本窗口）。</summary>
    public event Action? Unlocked;

    /// <param name="backdrop">桌面模糊快照兜底；仅实时模糊不可用时显示。</param>
    public LockWindow(AppSettings settings, InputBlocker blocker, ImageSource? backdrop)
    {
        _settings = settings;
        _blocker = blocker;
        _backdrop = backdrop;

        InitializeComponent();

        if (!settings.BlurBackground)
        {
            // 用户关闭模糊背景：整窗铺纯深色（不透明观感）
            Background = SolidBackdropBrush;
        }
        else if (backdrop != null)
        {
            // 先隐藏，等亚克力应用失败再显示（OnSourceInitialized 里决定）
            BackdropImage.Source = backdrop;
        }

        if (!string.IsNullOrWhiteSpace(settings.Hint))
        {
            HintText.Text = settings.Hint;
            HintText.Visibility = Visibility.Visible;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += (_, _) => ReassertForeground();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
        };

        _guardTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _guardTimer.Tick += (_, _) => ReassertForeground();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => OnCooldownTick();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _blocker.IsOwnWindowForeground = IsOwnWindowForeground;
        ApplyVirtualScreenBounds();
        ApplyBackdrop();
    }

    /// <summary>
    /// 背景模式：开启模糊 → DWM 亚克力实时模糊（失败回退快照/纯色）；
    /// 关闭模糊 → 纯深色（构造函数已设置）。
    /// </summary>
    private void ApplyBackdrop()
    {
        if (!_settings.BlurBackground)
            return;

        if (AcrylicBlur.Enable(_hwnd, AcrylicTintArgb))
            return; // 实时模糊生效，窗口保持透明

        // 亚克力不可用（远程桌面/旧系统等）→ 回退快照，再不行纯色
        if (_backdrop != null)
            BackdropImage.Visibility = Visibility.Visible;
        else
            Background = SolidBackdropBrush;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        CenterPanelOnPrimary();

        if (_settings.KeepDisplayOn)
        {
            NativeMethods.SetThreadExecutionState(
                NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);
            _displayKeptOn = true;
        }

        ReassertForeground();
        Keyboard.Focus(PasswordInput);

        _guardTimer.Start();
        _clockTimer.Start();
    }

    /// <summary>铺满整个虚拟桌面（物理像素 → WPF 逻辑坐标）。</summary>
    private void ApplyVirtualScreenBounds()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        Left = x / scaleX;
        Top = y / scaleY;
        Width = w / scaleX;
        Height = h / scaleY;
    }

    /// <summary>密码面板居中显示在**主显示器**上（虚拟桌面可能横跨多屏）。</summary>
    private void CenterPanelOnPrimary()
    {
        Panel.UpdateLayout();

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        double primaryWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) / scaleX;
        double primaryHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / scaleY;

        // 主屏物理原点 (0,0) → 相对本窗口的逻辑坐标要减去窗口自身的 Left/Top
        double centerX = -Left + primaryWidth / 2.0;
        double centerY = -Top + primaryHeight / 2.0;

        Canvas.SetLeft(Panel, Math.Max(0, centerX - Panel.ActualWidth / 2.0));
        Canvas.SetTop(Panel, Math.Max(0, centerY - Panel.ActualHeight / 2.0));
    }

    /// <summary>把锁屏窗重新顶到最前并抢回焦点（防止被其他置顶窗口盖住）。</summary>
    private void ReassertForeground()
    {
        if (_unlocked || _hwnd == IntPtr.Zero)
            return;

        NativeMethods.ForceForeground(_hwnd);

        if (PasswordInput.IsEnabled && !PasswordInput.IsKeyboardFocusWithin && !_reveal)
            Keyboard.Focus(PasswordInput);
        else if (_reveal && PasswordReveal.IsEnabled && !PasswordReveal.IsKeyboardFocusWithin)
            Keyboard.Focus(PasswordReveal);
    }

    private bool IsOwnWindowForeground()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;
        if (foreground == _hwnd)
            return true;

        NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>把键盘焦点交给当前生效的输入框（明文框或密码框）。</summary>
    private void FocusPasswordInput()
    {
        if (_reveal)
            PasswordReveal.Focus();
        else if (PasswordInput.IsEnabled)
            PasswordInput.Focus();
    }

    /// <summary>"显示/隐藏"按钮：在密码框与明文框之间切换，内容互相同步。</summary>
    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        _reveal = !_reveal;

        if (_reveal)
        {
            PasswordReveal.Text = PasswordInput.Password;
            PasswordInput.Visibility = Visibility.Collapsed;
            PasswordReveal.Visibility = Visibility.Visible;
            RevealButton.Content = "隐藏";
            PasswordReveal.Focus();
            PasswordReveal.CaretIndex = PasswordReveal.Text.Length;
        }
        else
        {
            PasswordInput.Password = PasswordReveal.Text;
            PasswordReveal.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            RevealButton.Content = "显示";
            FocusPasswordInput();
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private void TryUnlock()
    {
        if (_unlocked || _cooldownSeconds > 0)
            return;

        string password = _reveal ? PasswordReveal.Text : PasswordInput.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowError("请输入密码");
            return;
        }

        if (PasswordHasher.Verify(password, _settings.PasswordHash, _settings.PasswordSalt))
        {
            _unlocked = true;
            _guardTimer.Stop();
            _clockTimer.Stop();
            _cooldownTimer.Stop();
            ReleaseDisplayKeepAlive();
            Unlocked?.Invoke();
            return;
        }

        _failCount++;
        PasswordInput.Clear();
        PasswordReveal.Clear();

        if (_failCount >= 3)
        {
            StartCooldown(Math.Min(5 * (_failCount - 2), 60));
            ShowError($"密码错误。已连续输错 {_failCount} 次，请等待 {_cooldownSeconds} 秒后重试");
        }
        else
        {
            ShowError($"密码错误（第 {_failCount} 次）");
        }

        FocusPasswordInput();
    }

    private void StartCooldown(int seconds)
    {
        _cooldownSeconds = seconds;
        PasswordInput.IsEnabled = false;
        PasswordReveal.IsEnabled = false;
        UnlockButton.IsEnabled = false;
        RevealButton.IsEnabled = false;
        _cooldownTimer.Start();
    }

    private void OnCooldownTick()
    {
        _cooldownSeconds--;

        if (_cooldownSeconds > 0)
        {
            ShowError($"输错次数过多，请等待 {_cooldownSeconds} 秒");
            return;
        }

        _cooldownTimer.Stop();
        _cooldownSeconds = 0;
        PasswordInput.IsEnabled = true;
        PasswordReveal.IsEnabled = true;
        UnlockButton.IsEnabled = true;
        RevealButton.IsEnabled = true;
        ErrorText.Visibility = Visibility.Collapsed;
        FocusPasswordInput();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        string[] week = { "日", "一", "二", "三", "四", "五", "六" };
        ClockText.Text = $"{now:yyyy-MM-dd}  星期{week[(int)now.DayOfWeek]}  {now:HH:mm:ss}";
    }

    private void ReleaseDisplayKeepAlive()
    {
        if (!_displayKeptOn)
            return;

        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        _displayKeptOn = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 未解锁一律不允许关闭（Alt+F4 已被钩子拦截，这里再兜一层）
        if (!_unlocked)
        {
            e.Cancel = true;
            ReassertForeground();
            return;
        }

        ReleaseDisplayKeepAlive();
    }
}
