using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace EasyPub.Desktop;

public sealed class SourceBackupWindow : Window
{
    private readonly DataGrid _list = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
    private readonly string[] _sources;
    private readonly SourceBackupStore _store = SourceBackupStore.CreateDefault();
    public SourceBackupWindow(string[] sources)
    {
        _sources = sources;
        Title = "原文备份管理";
        Width = 1000; Height = 590; MinWidth = 760; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var hint = new TextBlock
        {
            Text = "这些是原始 TXT 的备份，不是章节树。新备份集中保存，同一路径保留首次原文，并按内容校验值保留修复前快照，不随转换删除。\n以下列出当前项目书稿的备份及旧版 .bak；可导出为副本核对，或明确选择后移入回收站。章节树随项目自动保存。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15),
        };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var export = new Button { Content = "恢复为 TXT 副本…", Margin = new Thickness(4) };
        var remove = new Button { Content = "将所选备份移入回收站…", Margin = new Thickness(4) };
        var close = new Button { Content = "关闭", Margin = new Thickness(4) };
        export.Click += Export_Click; remove.Click += Remove_Click; close.Click += (_, _) => Close();
        buttons.Children.Add(export); buttons.Children.Add(remove); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        foreach (var (title, field, width) in new[] { ("原文", "Source", 220d), ("备份时间", "SavedAt", 150d), ("备份路径", "Path", 500d) })
            _list.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(field), Width = width });
        panel.Children.Add(_list); Content = panel;
        Reload();
    }
    private sealed record BackupRow(string Source, string Path, string SavedAt);
    private void Reload() => _list.ItemsSource = _sources.Distinct(StringComparer.OrdinalIgnoreCase)
        .SelectMany(source => _store.FindBackups(source).Select(path =>
            new BackupRow(source, path, File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss")))).ToArray();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItem is not BackupRow row)
        { InkDialog.Show(this, "请选择一份备份导出。", "恢复副本"); return; }
        var dialog = new SaveFileDialog { Filter = "TXT 文件|*.txt", FileName = System.IO.Path.GetFileNameWithoutExtension(row.Source) + "-恢复副本.txt" };
        if (dialog.ShowDialog(this) != true) return;
        if (_sources.Any(source => string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)))
        { InkDialog.Show(this, "请选择新的副本路径，不能覆盖项目原文。", "保留原文"); return; }
        try { File.Copy(row.Path, dialog.FileName, overwrite: true); }
        catch (Exception ex) { InkDialog.Show(this, ex.Message, "导出失败"); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _list.SelectedItems.OfType<BackupRow>().ToArray();
        if (selected.Length == 0) return;
        if (InkDialog.Show(this, $"将所选 {selected.Length} 份备份移入回收站；原文、项目和成品不变。删除集中备份后，下次编辑会以当时原文重新备份。\n是否继续？",
            "清理备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var row in selected)
                if (_store.FindBackups(row.Source).Contains(row.Path, StringComparer.OrdinalIgnoreCase))
                    FileSystem.DeleteFile(row.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            InkDialog.Show(this, "所选备份已移入回收站，可从回收站恢复。", "清理完成");
        }
        catch (Exception ex) { InkDialog.Show(this, "部分备份可能未移除：" + ex.Message, "清理结果"); }
        Reload();
    }
}
