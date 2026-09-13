using System;
using System.Windows;
using System.Windows.Interop;
using ScreenGuard.Native;

namespace ScreenGuard;

/// <summary>
/// 设置窗口。首次运行（firstRun=true）时用于强制设置解锁密码。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly bool _firstRun;

    public SettingsWindow(AppSettings settings, bool firstRun)
    {
        _settings = settings;
        _firstRun = firstRun;

        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.EnableDarkTitleBar(hwnd);
        };

        IdleBox.Text = settings.IdleMinutes.ToString();
        HintBox.Text = settings.Hint;
        KeepDisplayCheck.IsChecked = settings.KeepDisplayOn;
        BlurBackgroundCheck.IsChecked = settings.BlurBackground;
        AutoStartCheck.IsChecked = AutoStart.IsEnabled();

        if (firstRun)
        {
            Title = "3xgcafe Guard 首次设置";
            FirstRunNotice.Visibility = Visibility.Visible;
            OldPasswordRow.Visibility = Visibility.Collapsed;
            HintBox.Text = "";
        }
        else
        {
            OldPasswordRow.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => NewPassword.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string idleText = IdleBox.Text.Trim();
        if (!int.TryParse(idleText, out int minutes) || minutes < 1 || minutes > 180)
        {
            ShowError("锁定时间请填写 1 – 180 之间的整数（分钟）。");
            IdleBox.Focus();
            return;
        }

        string newPassword = NewPassword.Password;
        string confirm = ConfirmPassword.Password;
        bool wantsChange = newPassword.Length > 0 || confirm.Length > 0;

        if (wantsChange)
        {
            if (newPassword.Length < 4)
            {
                ShowError("新密码至少 4 位。");
                NewPassword.Focus();
                return;
            }
            if (newPassword != confirm)
            {
                ShowError("两次输入的新密码不一致。");
                ConfirmPassword.Clear();
                ConfirmPassword.Focus();
                return;
            }
            if (!_firstRun && !PasswordHasher.Verify(OldPassword.Password, _settings.PasswordHash, _settings.PasswordSalt))
            {
                ShowError("旧密码不正确，无法修改密码。");
                OldPassword.Clear();
                OldPassword.Focus();
                return;
            }

            (string hash, string salt) = PasswordHasher.Create(newPassword);
            _settings.PasswordHash = hash;
            _settings.PasswordSalt = salt;
        }
        else if (_firstRun || !_settings.HasPassword)
        {
            ShowError("请设置解锁密码。");
            NewPassword.Focus();
            return;
        }

        _settings.IdleMinutes = minutes;
        _settings.Hint = HintBox.Text.Trim();
        _settings.KeepDisplayOn = KeepDisplayCheck.IsChecked == true;
        _settings.BlurBackground = BlurBackgroundCheck.IsChecked == true;

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowError("设置保存失败：" + ex.Message);
            return;
        }

        try
        {
            AutoStart.SetEnabled(AutoStartCheck.IsChecked == true);
        }
        catch
        {
            // 注册表写入失败不阻断设置保存
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
