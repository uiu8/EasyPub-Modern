using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace EasyPub.Desktop;

/// <summary>
/// The backup folder, as a reader sees it.
///
/// Three things this window deliberately does NOT do.
///
/// It does not restore over a book. Writing a backup back onto a source file is a source mutation: it
/// needs the same transaction, journal and crash recovery as any other edit, because a half-restored
/// TXT with a stale chapter tree is worse than no restore at all. Until that layer exists (Phase 5)
/// the strongest action here is exporting a *copy* to a path the reader picks, which is what the
/// 「导出为 TXT 副本…」 button does. The old 「恢复为 TXT 副本…」 caption was already export-only; the
/// name now says so.
///
/// It does not delete without being told. Retention removes only the oldest snapshots beyond the
/// configured limit, never the baseline, and never while a transaction journal exists for the book.
///
/// It does not trust the index. What is listed is read from the folder, so a backup a reader can see
/// is never hidden by a missing manifest.
/// </summary>
public sealed class SourceBackupWindow : Window
{
    private readonly DataGrid _books = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single, Height = 150,
    };
    private readonly DataGrid _entries = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Extended,
        // The path column is the reason this window exists — it is how a reader finds the file. Letting
        // it be clipped at a fixed width defeated that, so the grid scrolls sideways instead and every
        // path also carries its full text as a tooltip.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock _locationText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _statusText = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly Button _exportOne = new() { Content = "导出为 TXT 副本…", Margin = new Thickness(4) };
    private readonly Button _exportAll = new() { Content = "导出所选书籍的全部备份…", Margin = new Thickness(4) };
    private readonly Button _openFolder = new() { Content = "打开备份位置", Margin = new Thickness(4) };
    private readonly Button _prune = new() { Content = "按保留数清理…", Margin = new Thickness(4) };
    private readonly Button _remove = new() { Content = "将所选备份移入回收站…", Margin = new Thickness(4) };

    private readonly string[] _sources;
    private SourceBackupStore _store = SourceBackupStore.CreateDefault();

    public SourceBackupWindow(string[] sources)
    {
        _sources = sources ?? [];
        Title = "原文备份";
        Width = 1060; Height = 640; MinWidth = 820; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 主题资源只在真正的应用里存在。没有 Application 时（例如被一个不建 Application 的测试
        // 直接构造）跳过引用，窗口就用默认画刷画 —— 数据本身不依赖主题，而依赖主题会让这个窗口
        // 在没有 Application 的宿主里连构造都失败。
        ApplyThemeResources();

        var panel = new DockPanel { Margin = new Thickness(20) };

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "这些是原始 TXT 的备份，不是章节树。首次准备修改某本书时保存一份原文，"
                + "每次修复前再按内容校验值保存一份快照；同一内容不重复保存，也不随转换删除。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        header.Children.Add(_locationText);
        var locationRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var change = new Button { Content = "更改备份位置…", Margin = new Thickness(0, 0, 8, 0) };
        change.Click += ChangeLocation_Click;
        var reset = new Button { Content = "恢复默认位置", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += ResetLocation_Click;
        locationRow.Children.Add(change); locationRow.Children.Add(reset);
        header.Children.Add(locationRow);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "关闭", Margin = new Thickness(4), IsCancel = true };
        close.Click += (_, _) => Close();
        footer.Children.Add(_exportOne); footer.Children.Add(_exportAll);
        footer.Children.Add(_openFolder); footer.Children.Add(_prune); footer.Children.Add(_remove);
        footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);

        var status = _statusText;
        DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);

        foreach (var (title, field, width) in new[]
                 {
                     ("书稿", "SourceName", 260d), ("首次原文", "BaselineLabel", 150d),
                     ("快照数", "SnapshotLabel", 70d), ("最近备份", "UpdatedLabel", 150d),
                     ("原文是否还在", "SourceStateLabel", 110d),
                 })
            _books.Columns.Add(new DataGridTextColumn
            {
                Header = title, Binding = new System.Windows.Data.Binding(field), IsReadOnly = true, Width = width,
            });
        foreach (var (title, field, width, isPath) in new[]
                 {
                     ("种类", "KindLabel", 100d, false), ("时间", "SavedAtLabel", 150d, false),
                     ("大小", "SizeLabel", 80d, false), ("内容校验值", "ShortHash", 100d, false),
                     ("路径", "Path", 620d, true),
                 })
        {
            var column = new DataGridTextColumn
            {
                Header = title, Binding = new System.Windows.Data.Binding(field), IsReadOnly = true, Width = width,
            };
            if (isPath)
                // Read-only text columns need the tooltip set through the generated element; a style is
                // the only hook that survives the recycling the grid does while scrolling.
                column.ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(FrameworkElement.ToolTipProperty,
                            new System.Windows.Data.Binding(field)),
                    },
                };
            _entries.Columns.Add(column);
        }

        var split = new DockPanel();
        DockPanel.SetDock(_books, Dock.Top);
        split.Children.Add(_books);
        var entriesLabel = new TextBlock
        {
            Text = "所选书籍的备份（首次原文固定列在最后，不参与清理）",
            Margin = new Thickness(0, 10, 0, 4),
        };
        DockPanel.SetDock(entriesLabel, Dock.Top);
        split.Children.Add(entriesLabel);
        split.Children.Add(_entries);
        panel.Children.Add(split);

        Content = panel;
        _books.SelectionChanged += (_, _) => ReloadEntries();
        _exportOne.Click += ExportOne_Click;
        _exportAll.Click += ExportAll_Click;
        _openFolder.Click += (_, _) => OpenInExplorer(_store.DirectoryPath);
        _prune.Click += Prune_Click;
        _remove.Click += Remove_Click;
        Reload();
    }

    private sealed record BookRow(
        string SourcePath, string SourceName, string BaselineLabel, string SnapshotLabel,
        string UpdatedLabel, string SourceStateLabel);

    private BookRow? Selected => _books.SelectedItem as BookRow;

    private void Reload()
    {
        _store = SourceBackupStore.CreateDefault();
        _locationText.Text = $"备份位置：{_store.DirectoryPath}"
            + $"（保留最近 {_store.SnapshotRetentionLimit} 份快照）";

        var rows = new List<BookRow>();
        foreach (var source in _sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entries = _store.Inventory().ListEntries(source);
            if (entries.Count == 0) continue;
            var baseline = entries.FirstOrDefault(entry => entry.Kind == SourceBackupKind.Baseline);
            var snapshots = entries.Count(entry => entry.Kind == SourceBackupKind.Snapshot);
            var newest = entries.Max(entry => entry.SavedAt);
            rows.Add(new BookRow(
                source,
                Path.GetFileName(source),
                baseline?.SavedAtLabel ?? "（无）",
                snapshots.ToString(),
                newest.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                File.Exists(source) ? "在" : "已不在"));
        }
        _books.ItemsSource = rows;
        if (rows.Count > 0) _books.SelectedIndex = 0; else ReloadEntries();
        _statusText.Text = rows.Count == 0
            ? "当前项目书稿还没有备份。首次执行修复或创建编辑副本时会自动保存一份原文。"
            : $"共 {rows.Count} 本书稿有备份。";
    }

    private void ReloadEntries()
    {
        var row = Selected;
        _entries.ItemsSource = row is null ? null : _store.Inventory().ListEntries(row.SourcePath);
        var hasEntries = row is not null && _entries.Items.Count > 0;
        _exportOne.IsEnabled = hasEntries;
        _exportAll.IsEnabled = hasEntries;
        _remove.IsEnabled = hasEntries;
        _prune.IsEnabled = row is not null;
    }

    private void ChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择备份位置" };
        if (picker.ShowDialog(this) != true) return;
        SaveLocation(picker.FolderName);
    }

    private void ResetLocation_Click(object sender, RoutedEventArgs e) => SaveLocation(null);

    /// <summary>
    /// Stores the chosen root in app settings. Existing backups are NOT moved: moving files a reader
    /// may already be relying on, without being asked, is the kind of helpfulness that loses data. The
    /// new location applies to backups taken from now on, and both the window and the status line say
    /// where the current one is.
    /// </summary>
    private void SaveLocation(string? root)
    {
        try
        {
            var store = AppSettingsStore.CreateDefault();
            var settings = store.Load();
            store.SaveAsync(settings with { SourceBackupRoot = string.IsNullOrWhiteSpace(root) ? null : root })
                .GetAwaiter().GetResult();
            Reload();
            _statusText.Text = "备份位置已更改。已有备份没有移动 —— 它们仍在原来的位置，"
                + "需要的话请自行复制。此后的备份保存到新位置。";
        }
        catch (Exception ex) { InkDialog.Show(this, ex.Message, "无法保存备份位置"); }
    }

    private void ExportOne_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.SelectedItems.Count != 1 || _entries.SelectedItem is not SourceBackupEntry entry
            || Selected is not { } book)
        { InkDialog.Show(this, "请选择一份备份导出。", "导出副本"); return; }
        var dialog = new SaveFileDialog
        {
            Filter = "TXT 文件|*.txt",
            FileName = Path.GetFileNameWithoutExtension(book.SourceName)
                + "-" + (entry.Kind == SourceBackupKind.Baseline ? "首次原文" : entry.ShortHash) + ".txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        Export(new[] { entry.Path }, dialog.FileName);
    }

    /// <summary>
    /// Exports every backup of the selected book into a folder the reader picks, one file each, named
    /// so the order and kind are readable without opening them.
    /// </summary>
    private void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } book) return;
        var entries = _store.Inventory().ListEntries(book.SourcePath);
        if (entries.Count == 0) return;
        var picker = new OpenFolderDialog { Title = "选择导出到的文件夹" };
        if (picker.ShowDialog(this) != true) return;
        var stem = Path.GetFileNameWithoutExtension(book.SourceName);
        var index = 0;
        var written = new List<string>();
        foreach (var entry in entries.OrderBy(entry => entry.SavedAt))
        {
            var name = entry.Kind == SourceBackupKind.Baseline
                ? $"{stem}-首次原文.txt"
                : $"{stem}-{entry.SavedAtLabel.Replace(':', '-').Replace(' ', '_')}-{entry.ShortHash}.txt";
            var target = UniquePath(Path.Combine(picker.FolderName, name));
            try { File.Copy(entry.Path, target, overwrite: false); written.Add(target); index++; }
            catch (Exception ex) { InkDialog.Show(this, ex.Message, "部分备份未导出"); break; }
        }
        _statusText.Text = written.Count == 0
            ? "没有导出任何文件。"
            : $"已导出 {written.Count} 份备份到 {picker.FolderName}。";
    }

    private void Export(IReadOnlyList<string> paths, string destination)
    {
        if (paths.Count == 0) return;
        var full = Path.GetFullPath(destination);
        // Guarding against overwriting the book itself was the whole point of the old caption; keep it,
        // because exporting onto the source is the one destination that would destroy data.
        if (_sources.Any(source => string.Equals(Path.GetFullPath(source), full, StringComparison.OrdinalIgnoreCase)))
        { InkDialog.Show(this, "请选择新的副本路径，不能覆盖项目原文。", "保留原文"); return; }
        try
        {
            File.Copy(paths[0], full, overwrite: true);
            _statusText.Text = $"已导出副本：{full}";
        }
        catch (Exception ex) { InkDialog.Show(this, ex.Message, "导出失败"); }
    }

    private void Prune_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } book) return;
        var limit = _store.SnapshotRetentionLimit;
        if (InkDialog.Show(this,
                $"每本书保留最近 {limit} 份快照，更早的移入回收站。首次原文永不删除。\n是否继续？",
                "按保留数清理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = _store.Inventory().Prune(book.SourcePath, limit);
        Reload();
        _statusText.Text = result.RefusedBecause
            ?? (result.Removed.Count == 0
                ? $"没有需要清理的快照（当前保留上限 {limit} 份）。"
                : $"已移入回收站 {result.Removed.Count} 份较早的快照，保留 {result.Kept.Count + 1} 份。");
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _entries.SelectedItems.OfType<SourceBackupEntry>().ToArray();
        if (selected.Length == 0) return;
        var baselines = selected.Count(entry => entry.Kind == SourceBackupKind.Baseline);
        var warning = baselines > 0
            ? $"所选包含 {baselines} 份「首次原文」，删除后无法再取得该版本。\n"
            : "";
        if (InkDialog.Show(this,
                $"{warning}将所选 {selected.Length} 份备份移入回收站；书稿、项目和成品不变。\n是否继续？",
                "清理备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var entry in selected)
                FileSystem.DeleteFile(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Reload();
            _statusText.Text = "所选备份已移入回收站，可从回收站恢复。";
        }
        catch (Exception ex)
        {
            Reload();
            InkDialog.Show(this, "部分备份可能未移除：" + ex.Message, "清理结果");
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { InkDialog.Show(this, ex.Message, "无法打开备份位置"); }
    }

    /// <summary>
    /// Adopts the application's theme brushes when there is an application to ask. Called from the
    /// constructor only; resolved once, because a resource reference set later would need a re-layout.
    /// </summary>
    private void ApplyThemeResources()
    {
        if (Application.Current is null) return;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
    }
}
