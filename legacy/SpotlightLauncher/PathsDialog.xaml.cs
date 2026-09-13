using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SpotlightLauncher.Indexing;

namespace SpotlightLauncher;

/// <summary>自定义扫描路径管理对话框（极简：添加文件/文件夹、删除）。</summary>
public partial class PathsDialog : Window
{
    private readonly ScanPathsStore _store;

    /// <summary>关闭时是否发生过增删（主窗据此决定是否重建索引）。</summary>
    public bool Changed { get; private set; }

    public PathsDialog(ScanPathsStore store)
    {
        InitializeComponent();
        _store = store;
        RefreshList();
    }

    private void RefreshList()
    {
        var rows = _store.Paths
            .Select(p => new PathRow
            {
                Path = p,
                Kind = Directory.Exists(p) ? "文件夹" : File.Exists(p) ? "文件" : "不存在",
            })
            .ToList();
        PathList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "添加可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
            if (_store.Add(f)) Changed = true;
        RefreshList();
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "添加文件夹(递归扫描其下 exe)" };
        if (dlg.ShowDialog(this) != true) return;
        if (_store.Add(dlg.FolderName)) Changed = true;
        RefreshList();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string p } && _store.Remove(p))
        {
            Changed = true;
            RefreshList();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标状态异常时忽略 */ }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}

/// <summary>路径列表行:完整路径 + 类型标签。</summary>
public sealed class PathRow
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "";
}
