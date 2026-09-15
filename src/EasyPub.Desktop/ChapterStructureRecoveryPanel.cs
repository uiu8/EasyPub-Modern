using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private void StructureEvidence_Click(object sender, RoutedEventArgs e)
    {
        var issue = _allReviewIssues.FirstOrDefault(i => i.Code == "chapter_structure_suggested");
        if (issue is not null) NavigateToSuggestion(issue);
    }

    private void DismissStructure_Click(object sender, RoutedEventArgs e)
    {
        var group = _reviewGroups.FirstOrDefault(g => g.Issue.Code == "chapter_structure_suggested");
        if (group is null) return;
        _confirmedGroups[ReviewKey(group)] = group;
        RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
        SetReviewResult("本次暂不提示结构整理；可从 更多 → 视图 恢复已确认提醒。原文或相关结构变化后重新评估。");
    }

    private async void RecoverStructure_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged) return;
        ChapterStructureRecoveryResult result;
        IsEnabled = false;
        try { result = await Task.Run(() => ChapterStructureRecovery.Analyze(_document)); }
        catch (Exception ex) { ShowInfo(ex.Message, "无法重建结构"); return; }
        finally { IsEnabled = true; }
        var dialog = ThemedWindow("预览编号分组与结构修复", 900, 680);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "应用重建方案", Margin = new Thickness(8) };
        var cancel = new Button { Content = "取消", Margin = new Thickness(8) };
        apply.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var hint = new TextBlock { Text = $"找到 {result.Groups} 个编号段。按原文顺序重建，将替换当前手工层级和标题，可整批撤销。\n编号段不等于正式卷；卷名请核对后编辑。通知与额外章头保留为正文，同名章节不删除。\n此功能不能恢复原文缺失、截断或混入其他书的内容。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        if (result.Notes.Any(n => n.StartsWith("连续重复区段")))
            hint.Text = "检测到连续重复区段：请先核对源文件重复拼接。重建只整理层级，不会去除重复内容，也不能据此推断真实卷名。\n\n" + hint.Text;
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "处理说明", Content = new ListBox { ItemsSource = result.Notes.Select(note =>
            new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, MaxWidth = 770, Margin = new Thickness(4, 6, 4, 6) }) } });
        tabs.Items.Add(new TabItem { Header = "重建后的完整目录", Content = new ListBox { ItemsSource = result.Entries.Select(entry =>
            (entry.Level == 2 && !entry.IsFrontMatter ? "　　" : "") + entry.Title + (entry.TitleLineNumber is int line ? $"  · 原文第 {line} 行" : "")) } });
        panel.Children.Add(tabs);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        Mutate(() =>
        {
            Roots.Clear();
            foreach (var root in BuildTree(result.Entries)) { root.IsExpanded = false; Roots.Add(root); }
            _selectedNode = Roots.FirstOrDefault(n => !n.IsFrontMatter);
            SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        });
        ChapterSearchText.Text = ""; IssueCategoryCombo.SelectedIndex = 0;
        ApplySearchFilter(); AllChapters_Click(this, new RoutedEventArgs());
        RefreshSelectedLines(); UpdateActionButtons();
        SetReviewResult($"已建立 {result.Groups} 个编号段。请先核对分组，再检查剩余提醒；应用并返回后生效，可撤销。");
    }
}
