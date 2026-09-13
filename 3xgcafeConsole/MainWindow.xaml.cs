using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ThreeXGCafeConsole;

/// <summary>
/// 3xgcafe 统一管理面板：进程级启停/自启 + 命名管道状态与快捷指令。
/// 工具清单由 %APPDATA%\3xgcafe\Console\tools.json 驱动 —— 新增工具无需改代码。
/// </summary>
public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string PanelRunValue = "3xgcafe-Console";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly SolidColorBrush OkBrush = new(Color.FromRgb(0x6F, 0xBF, 0x73));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0xE3, 0xC4, 0x43));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x8A, 0x90, 0x99));
    private static readonly SolidColorBrush DimBrush = new(Color.FromRgb(0x6B, 0x70, 0x79));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xE8, 0xE8, 0xEC));

    private ToolConfig _config = new();
    private readonly List<ToolCard> _cards = new();
    private readonly DispatcherTimer _timer;
    private bool _polling;

    public MainWindow()
    {
        InitializeComponent();

        _config = ToolConfigStore.Load();
        BuildCards();

        PanelAutoStartCheck.IsChecked = IsRunEnabled(PanelRunValue);

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await PollAsync();

        Loaded += async (_, _) =>
        {
            _timer.Start();
            await PollAsync();
        };
    }

    // ---------------- 卡片构建 ----------------

    private sealed class ToolCard
    {
        public ToolDefinition Def { get; init; } = new();
        public Border Root { get; init; } = null!;
        public Rectangle Led { get; init; } = null!;
        public TextBlock StateText { get; init; } = null!;
        public TextBlock DetailText { get; init; } = null!;
        public Button StartButton { get; init; } = null!;
        public Button StopButton { get; init; } = null!;
        public Button RestartButton { get; init; } = null!;
        public CheckBox AutoStartCheck { get; init; } = null!;
        public StackPanel ActionHost { get; init; } = null!;
        public List<Button> ActionButtons { get; } = new();
        public bool Running { get; set; }
        public bool PipeOk { get; set; }
        public bool Locked { get; set; }
    }

    private void BuildCards()
    {
        CardHost.Children.Clear();
        _cards.Clear();

        foreach (ToolDefinition def in _config.Tools)
            _cards.Add(BuildCard(def));

        UpdateHeader();
    }

    private ToolCard BuildCard(ToolDefinition def)
    {
        var led = new Rectangle
        {
            Width = 8,
            Height = 8,
            Fill = DimBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            Text = def.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = def.Subtitle,
            FontSize = 11.5,
            Foreground = MutedBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var stateText = new TextBlock
        {
            Text = "STOPPED",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var headRow = new Grid();
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(led, 0);
        Grid.SetColumn(nameText, 1);
        Grid.SetColumn(subtitleText, 2);
        Grid.SetColumn(stateText, 3);
        headRow.Children.Add(led);
        headRow.Children.Add(nameText);
        headRow.Children.Add(subtitleText);
        headRow.Children.Add(stateText);

        var detailText = new TextBlock
        {
            Text = "—",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 10, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var actionHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
        var actionButtons = new List<Button>();
        ToolCard? cardRef = null;
        foreach (ToolAction action in def.QuickActions)
        {
            var btn = new Button
            {
                Content = action.Label,
                Style = (Style)FindResource("ConsoleButton"),
                Margin = new Thickness(0, 0, 6, 0),
                IsEnabled = false
            };
            ToolAction captured = action;
            btn.Click += async (_, _) =>
            {
                if (cardRef != null)
                    await RunActionAsync(cardRef, captured);
            };
            actionHost.Children.Add(btn);
            actionButtons.Add(btn);
        }

        var startButton = new Button { Content = "启动", Style = (Style)FindResource("ConsoleButton"), Margin = new Thickness(0, 0, 6, 0) };
        var stopButton = new Button { Content = "停止", Style = (Style)FindResource("ConsoleButton"), Margin = new Thickness(0, 0, 6, 0) };
        var restartButton = new Button { Content = "重启", Style = (Style)FindResource("ConsoleButton"), Margin = new Thickness(0, 0, 10, 0) };
        var autoStartCheck = new CheckBox
        {
            Content = "自启",
            Style = (Style)FindResource("ConsoleCheckBox"),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = IsRunEnabled(def.EffectiveRunValueName)
        };

        var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        controlRow.Children.Add(startButton);
        controlRow.Children.Add(stopButton);
        controlRow.Children.Add(restartButton);
        controlRow.Children.Add(autoStartCheck);

        if (!def.BuiltIn)
        {
            var removeButton = new Button { Content = "移除", Style = (Style)FindResource("ConsoleButton"), Margin = new Thickness(10, 0, 0, 0) };
            removeButton.Click += (_, _) => RemoveTool(def);
            controlRow.Children.Add(removeButton);
        }

        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        body.Children.Add(headRow);
        body.Children.Add(detailText);
        body.Children.Add(actionHost);
        body.Children.Add(controlRow);

        var accentStrip = new Rectangle
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = AccentBrush,
            Opacity = 0.85
        };

        var notch = new Polygon
        {
            Points = new PointCollection { new Point(14, 0), new Point(14, 14), new Point(0, 0) },
            Fill = (Brush)FindResource("BgBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        var inner = new Grid();
        inner.Children.Add(accentStrip);
        inner.Children.Add(body);
        inner.Children.Add(notch);

        var root = new Border
        {
            Width = 336,
            Margin = new Thickness(0, 0, 12, 12),
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Child = inner
        };

        CardHost.Children.Add(root);

        var card = new ToolCard
        {
            Def = def,
            Root = root,
            Led = led,
            StateText = stateText,
            DetailText = detailText,
            StartButton = startButton,
            StopButton = stopButton,
            RestartButton = restartButton,
            AutoStartCheck = autoStartCheck,
            ActionHost = actionHost
        };
        card.ActionButtons.AddRange(actionButtons);
        cardRef = card;

        startButton.Click += async (_, _) => await StartToolAsync(card);
        stopButton.Click += async (_, _) => await StopToolAsync(card);
        restartButton.Click += async (_, _) =>
        {
            await StopToolAsync(card, askWhenLocked: false);
            await Task.Delay(400);
            await StartToolAsync(card);
        };
        autoStartCheck.Click += (_, _) => ApplyAutoStart(card);

        return card;
    }

    // ---------------- 状态轮询 ----------------

    private async Task PollAsync()
    {
        if (_polling)
            return;

        _polling = true;
        try
        {
            await Task.WhenAll(_cards.Select(RefreshCardAsync));
            UpdateHeader();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task RefreshCardAsync(ToolCard card)
    {
        Process? process = FindProcess(card.Def.ProcessName);

        if (process == null)
        {
            card.Running = false;
            card.PipeOk = false;
            card.Locked = false;
            SetVisual(card, "STOPPED", "未运行", DimBrush);
            card.StartButton.IsEnabled = true;
            card.StopButton.IsEnabled = false;
            card.RestartButton.IsEnabled = false;
            SetActionsEnabled(card, false);
            return;
        }

        string processInfo = $"PID {process.Id}";
        try
        {
            long mb = process.WorkingSet64 / (1024 * 1024);
            TimeSpan up = DateTime.Now - process.StartTime;
            processInfo += $" · 内存 {mb} MB · 已运行 {FormatSpan(up)}";
        }
        catch
        {
            // 读取进程信息失败（权限等）时仅显示 PID
        }

        ToolReply? reply = await PipeClient.QueryAsync(card.Def.PipeName, "status");

        card.Running = true;
        card.StartButton.IsEnabled = false;
        card.StopButton.IsEnabled = true;
        card.RestartButton.IsEnabled = true;

        if (reply == null)
        {
            card.PipeOk = false;
            card.Locked = false;
            SetVisual(card, "RUNNING · 无通道", processInfo, AccentBrush);
            SetActionsEnabled(card, false);
            return;
        }

        card.PipeOk = reply.Ok;
        card.Locked = string.Equals(reply.State, "locked", StringComparison.OrdinalIgnoreCase);
        SetActionsEnabled(card, reply.Ok);

        string stateLabel = reply.State.ToUpperInvariant() switch
        {
            "LOCKED" => "LOCKED",
            "PAUSED" => "PAUSED",
            "STOPPING" => "STOPPING",
            _ => "RUNNING"
        };

        Brush stateBrush = reply.State.ToLowerInvariant() switch
        {
            "locked" => AccentBrush,
            "paused" => AccentBrush,
            _ => OkBrush
        };

        string detail = string.IsNullOrWhiteSpace(reply.Detail) ? processInfo : reply.Detail;

        SetVisual(card, stateLabel, detail, stateBrush);
    }

    private void SetVisual(ToolCard card, string state, string detail, Brush stateBrush)
    {
        card.StateText.Text = state;
        card.StateText.Foreground = stateBrush;
        card.DetailText.Text = detail;
        card.DetailText.Foreground = card.Running ? MutedBrush : DimBrush;
        card.Led.Fill = card.Running ? stateBrush : DimBrush;
        card.Root.BorderBrush = card.Running
            ? (ReferenceEquals(stateBrush, OkBrush) ? (Brush)FindResource("LineBrush") : stateBrush)
            : (Brush)FindResource("LineBrush");
    }

    private static void SetActionsEnabled(ToolCard card, bool enabled)
    {
        foreach (Button b in card.ActionButtons)
            b.IsEnabled = enabled && card.Running;
    }

    // ---------------- 启停 / 指令 ----------------

    private async Task StartToolAsync(ToolCard card)
    {
        if (!File.Exists(card.Def.ExePath))
        {
            MessageBox.Show($"找不到可执行文件：\n{card.Def.ExePath}\n\n可在 tools.json 中修正 exePath。",
                card.Def.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FindProcess(card.Def.ProcessName) != null)
        {
            await RefreshCardAsync(card);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(card.Def.ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(card.Def.ExePath) ?? ToolConfigStore.DefaultToolDir
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}", card.Def.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await Task.Delay(1200);
        await RefreshCardAsync(card);
    }

    /// <summary>优先通过通道优雅退出，失败或超时再结束进程。</summary>
    private async Task StopToolAsync(ToolCard card, bool askWhenLocked = true)
    {
        if (askWhenLocked && card.Locked)
        {
            MessageBoxResult r = MessageBox.Show(
                $"{card.Def.Name} 当前处于「已锁定」状态。\n\n停止它会使锁屏立即失效（相当于绕过本机防护）。\n确定要继续吗？",
                "安全确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
                return;
        }

        if (card.PipeOk)
        {
            await PipeClient.QueryAsync(card.Def.PipeName, "quit", null, 900);
            await Task.Delay(900);
        }

        Process? p = FindProcess(card.Def.ProcessName);
        if (p != null)
        {
            try { p.Kill(); }
            catch (Exception ex)
            {
                MessageBox.Show($"结束进程失败：{ex.Message}", card.Def.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            await Task.Delay(500);
        }

        await RefreshCardAsync(card);
    }

    private async Task RunActionAsync(ToolCard card, ToolAction action)
    {
        ToolReply? reply = await PipeClient.QueryAsync(card.Def.PipeName, action.Cmd, action.Args, 1500);
        if (reply == null || !reply.Ok)
        {
            MessageBox.Show($"指令执行失败：{action.Cmd}\n{reply?.Detail}", card.Def.Name,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await Task.Delay(400);
        await RefreshCardAsync(card);
    }

    // ---------------- 自启 / 配置 ----------------

    private void ApplyAutoStart(ToolCard card)
    {
        bool enable = card.AutoStartCheck.IsChecked == true;
        SetRun(card.Def.EffectiveRunValueName, card.Def.ExePath, enable);
        card.AutoStartCheck.IsChecked = IsRunEnabled(card.Def.EffectiveRunValueName);
    }

    private void PanelAutoStart_Click(object sender, RoutedEventArgs e)
    {
        bool enable = PanelAutoStartCheck.IsChecked == true;
        SetRun(PanelRunValue, Environment.ProcessPath ?? "", enable);
        PanelAutoStartCheck.IsChecked = IsRunEnabled(PanelRunValue);
    }

    private void RemoveTool(ToolDefinition def)
    {
        if (def.BuiltIn)
            return;

        MessageBoxResult r = MessageBox.Show($"从面板中移除 {def.Name}？（不会删除程序文件）",
            "移除工具", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes)
            return;

        _config.Tools.RemoveAll(t => t.Id == def.Id);
        ToolConfigStore.Save(_config);
        BuildCards();
    }

    // ---------------- 顶栏 / 底栏 ----------------

    private void UpdateHeader()
    {
        int online = _cards.Count(c => c.Running);
        OnlineText.Text = $"{online:D2} / {_cards.Count:D2} online";
        StatusText.Text = $"最后同步 {DateTime.Now:HH:mm:ss} · 配置 {ToolConfigStore.ConfigPath}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await PollAsync();

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (ToolCard card in _cards.Where(c => !c.Running))
            await StartToolAsync(card);
        await PollAsync();
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        List<ToolCard> running = _cards.Where(c => c.Running).ToList();
        if (running.Count == 0)
            return;

        if (running.Any(c => c.Locked))
        {
            MessageBoxResult r = MessageBox.Show(
                "有工具处于「已锁定」状态（ScreenGuard）。全部停止会使锁屏立即失效。\n确定要继续吗？",
                "安全确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
                return;
        }

        foreach (ToolCard card in running)
            await StopToolAsync(card, askWhenLocked: false);

        await PollAsync();
    }

    private void AddTool_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddToolWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;

        if (_config.Tools.Any(t => string.Equals(t.Id, dialog.Result.Id, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("已存在同 Id 的工具。", "添加工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.Tools.Add(dialog.Result);
        ToolConfigStore.Save(_config);
        BuildCards();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(ToolConfigStore.DefaultToolDir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ToolConfigStore.DefaultToolDir}\"") { UseShellExecute = true });
            else
                MessageBox.Show($"目录不存在：{ToolConfigStore.DefaultToolDir}", "打开目录",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // 打开资源管理器失败不影响使用
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- 工具方法 ----------------

    private static Process? FindProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        try
        {
            return Process.GetProcessesByName(processName).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h{span.Minutes:D2}m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m{span.Seconds:D2}s";
        return $"{span.Seconds}s";
    }

    private static bool IsRunEnabled(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(valueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetRun(string valueName, string exePath, bool enable)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
            {
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(valueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表写入失败不阻断面板
        }
    }
}
