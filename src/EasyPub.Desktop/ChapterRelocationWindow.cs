using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

internal sealed class ChapterRelocationWindow : Window
{
    internal ChapterTreeRelocationPreview? Preview { get; private set; }
    internal bool ExportCopy { get; private set; }
    private sealed record Destination(string Id, string Label) { public override string ToString() => Label; }

    internal ChapterRelocationWindow(ChapterTreeDocument document, IReadOnlyCollection<string> selected)
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "移动章节 · 仅调整成品顺序";
        Width = 760; Height = 620; MinWidth = 540; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        var panel = new DockPanel { Margin = new Thickness(20) };
        Content = panel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8) };
        var apply = new Button { Content = "确认移动（仅成品）", IsEnabled = false, Margin = new Thickness(8) };
        var export = new Button { Content = "另存修订副本…", IsEnabled = false, Margin = new Thickness(8) };
        RegisterName("RelocationApply", apply);
        RegisterName("RelocationCancel", cancel);
        RegisterName("RelocationExport", export);
        cancel.Click += (_, _) => DialogResult = false;
        apply.Click += (_, _) => { if (Preview != null) DialogResult = true; };
        export.Click += (_, _) => { if (Preview != null) { ExportCopy = true; DialogResult = true; } };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        buttons.Children.Insert(1, export);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        RegisterName("RelocationSummary", summary);
        DockPanel.SetDock(summary, Dock.Bottom); panel.Children.Add(summary);
        var header = new StackPanel();
        var moving = document.Entries.Where(e => selected.Contains(e.Id)).ToArray();
        header.Children.Add(new TextBlock { Text = $"移动选中的 {moving.Length} 章", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "选择目标章节，整组选章将移到它之前，并归入目标所在的卷。\n只调整章节树和成品阅读顺序，不修改 TXT。应用后可撤销。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        header.Children.Add(new TextBlock { Text = moving.Length == 0 ? "尚未选中章节" : $"选区：{moving[0].Title} → {moving[^1].Title}", TextWrapping = TextWrapping.Wrap });
        var search = new TextBox { Margin = new Thickness(0, 12, 0, 10), ToolTip = "搜索目标卷名或章节标题" };
        RegisterName("RelocationSearch", search);
        header.Children.Add(new TextBlock { Text = "搜索目标卷名或章节标题", Margin = new Thickness(0, 12, 0, 0) });
        header.Children.Add(search);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var destinations = new List<Destination>();
        var parents = new List<ChapterTreeEntry>();
        for (var i = 0; i < document.Entries.Count; i++)
        {
            var entry = document.Entries[i];
            if (entry.IsFrontMatter) { parents.Clear(); continue; }
            while (parents.Count > 0 && parents[^1].Level >= entry.Level) parents.RemoveAt(parents.Count - 1);
            var parent = i + 1 < document.Entries.Count && !document.Entries[i + 1].IsFrontMatter && document.Entries[i + 1].Level > entry.Level;
            if (!parent && !selected.Contains(entry.Id)) destinations.Add(new(entry.Id,
                string.Join(" / ", parents.Select(p => p.Title).Append(entry.Title)) + (entry.TitleLineNumber is int line ? $" · 原文 {line} 行" : " · 人工章节")));
            parents.Add(entry);
        }
        var list = new ListBox { ItemsSource = destinations };
        RegisterName("RelocationTargets", list);
        panel.Children.Add(list);
        void Update()
        {
            Preview = null; apply.IsEnabled = export.IsEnabled = false;
            if (list.SelectedItem is not Destination target) { summary.Text = "请选择目标章节；未选择时不会移动。"; return; }
            try
            {
                var preview = ChapterTreeRelocation.Before(document, selected, target.Id);
                if (preview.Entries.Select(e => e.Id).SequenceEqual(document.Entries.Select(e => e.Id)))
                { summary.Text = "选中章节已经位于此处，无需移动。"; return; }
                Preview = preview;
                summary.Text = $"将 {moving.Length} 章移到：\n{target.Label}\n之前。正文不删减，原位置不保留重复章节项。";
                apply.IsEnabled = export.IsEnabled = true;
            }
            catch (InvalidOperationException error) { summary.Text = error.Message; }
        }
        list.SelectionChanged += (_, _) => Update();
        search.TextChanged += (_, _) =>
        {
            list.SelectedItem = null;
            list.ItemsSource = destinations.Where(d => d.Label.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            Update();
        };
        Update();
    }
}

public partial class ChapterEditorWindow
{
    internal ManualSplitCopyResult? LastRelocationCopy { get; private set; }
    private async void RelocateChapters_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged) return;
        var selected = OperationSelection().Select(n => n.Id).ToArray();
        if (selected.Length == 0) { SetReviewResult("请先用 Ctrl 或 Shift 选中要移动的连续章节。"); return; }
        var snapshot = _document.WithEntries(Flatten().Select(n => n.ToEntry()).ToArray());
        var dialog = new ChapterRelocationWindow(snapshot, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Preview is not { } preview) return;
        try
        {
            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(_document.SourcePath)));
            if (_sourceChanged || !string.Equals(currentHash, _document.SourceSha256, StringComparison.OrdinalIgnoreCase))
            { SetReviewResult("TXT 已变化，本次未移动。请先迁移或重新识别章节树。"); return; }
            if (dialog.ExportCopy)
            {
                IsEnabled = false;
                try
                {
                    var store = SourceBackupStore.CreateDefault();
                    var prepared = await Task.Run(() => ChapterRelocationCopy.Prepare(snapshot, selected, preview.BeforeEntryId, _encodingMode), CancellationToken.None);
                    var copy = await Task.Run(() => prepared.ExportAsync(store), CancellationToken.None);
                    LastRelocationCopy = copy;
                    if (!copy.Succeeded)
                    {
                        if (IsVisible && Dispatcher.CheckAccess()) SetReviewResult("修订副本未完成，当前树未改变。" + copy.Transaction.Message + " 副本位置：" + copy.CopyPath);
                        return;
                    }
                    if (IsVisible && Dispatcher.CheckAccess()) SetReviewResult("已生成修订副本，当前原稿树未改变。打开章节项目继续：" + copy.ProjectPath
                        + "；移动清单：" + copy.CopyPath + ".movement.json；修改前备份：" + copy.Transaction.BackupPath);
                    return;
                }
                catch (Exception error) { if (IsVisible && Dispatcher.CheckAccess()) SetReviewResult("未生成修订副本：" + error.Message); return; }
                finally { if (IsVisible && Dispatcher.CheckAccess()) IsEnabled = true; }
            }
            Mutate(() =>
            {
                Roots.Clear();
                foreach (var root in BuildTree(preview.Entries)) Roots.Add(root);
                var moved = Flatten().Where(n => preview.MovedEntryIds.Contains(n.Id)).ToArray();
                _selectedNode = moved.FirstOrDefault();
                SetOperationSelection(moved);
            });
            SetReviewResult($"已将 {selected.Length} 章移至「{preview.TargetTitle}」之前。仅调整成品顺序，TXT 未改；可撤销。");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (IsVisible && Dispatcher.CheckAccess()) SetReviewResult("未完成移动：" + error.Message); }
    }
}
