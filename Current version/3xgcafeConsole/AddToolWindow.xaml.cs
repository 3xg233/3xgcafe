using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace ThreeXGCafeConsole;

/// <summary>添加工具对话框：结果通过 <see cref="Result"/> 返回。</summary>
public partial class AddToolWindow : Window
{
    public ToolDefinition? Result { get; private set; }

    public AddToolWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择工具的可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(ToolConfigStore.DefaultToolDir)
                ? ToolConfigStore.DefaultToolDir
                : null
        };

        if (dialog.ShowDialog(this) != true)
            return;

        PathBox.Text = dialog.FileName;

        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);

        if (string.IsNullOrWhiteSpace(PipeBox.Text))
            PipeBox.Text = "3xgcafe-" + Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        string path = PathBox.Text.Trim();

        if (name.Length == 0)
        {
            ShowError("请填写名称。");
            return;
        }

        if (path.Length == 0 || !File.Exists(path))
        {
            ShowError("可执行文件不存在，请重新选择。");
            return;
        }

        Result = new ToolDefinition
        {
            Id = Path.GetFileNameWithoutExtension(path),
            Name = name,
            Subtitle = SubtitleBox.Text.Trim(),
            ExePath = path,
            PipeName = PipeBox.Text.Trim(),
            RunValueName = Path.GetFileNameWithoutExtension(path),
            BuiltIn = false
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
