using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    public string TextEditorPath { get; set; } = "notepad.exe";

    private void CleanDuplicateTitles_Click(object sender, RoutedEventArgs e) => CleanDuplicateTitles(null);

    private void CleanDuplicateTitles(IReadOnlySet<string>? scope)
    {
        if (_sourceChanged) return;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = scope is null ? "处理范围：全书" : $"处理范围：当前问题组（{scope.Count} 项），其他组不受影响", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "只处理相邻、同级、标题相同且无子章节的重复项。\n行数按左侧正文行数计算，不含标题。保留至少一章，所有正文均保留。", TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 14) };
        row.Children.Add(new TextBlock { Text = "正文行数范围：", VerticalAlignment = VerticalAlignment.Center });
        var min = new TextBox { Text = "0", Width = 70 };
        var max = new TextBox { Text = "0", Width = 70 };
        row.Children.Add(min); row.Children.Add(new TextBlock { Text = " 至 ", VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(max);
        panel.Children.Add(row);
        var ignoreNumber = new CheckBox { Content = "忽略章号，按标题文字匹配（保留括号分段序号）", Margin = new Thickness(0, 0, 0, 8) };
        var preferFirst = new CheckBox { Content = "采用每组前一个标题（默认采用保留正文项的标题）", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(ignoreNumber); panel.Children.Add(preferFirst);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(summary);
        var details = new ListBox { MaxHeight = 220, Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(details);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "清理", IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        var dialog = new Window { Title = "清理重复标题", Owner = this, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        var entries = Flatten().Select(n => n.ToEntry()).ToArray();
        IReadOnlyList<ChapterTreeEntry>? cleaned = null;
        void Preview()
        {
            cleaned = null; apply.IsEnabled = false; details.ItemsSource = null;
            if (!int.TryParse(min.Text, out var from) || !int.TryParse(max.Text, out var to) || from < 0 || to < from)
            { summary.Text = "请输入非负整数范围，下限不能大于上限。"; return; }
            var changes = new List<DuplicateTitleChange>();
            cleaned = DuplicateChapterCleaner.Clean(entries, from, to, ignoreNumber.IsChecked == true, preferFirst.IsChecked == true, changes, scope);
            details.ItemsSource = changes.Select(change => $"移除第 {change.RemovedLine} 行：{change.RemovedTitle}\n成品保留标题：{change.KeptTitle}").ToArray();
            var count = entries.Length - cleaned.Count;
            summary.Text = $"将清理 {count} 个重复标题，正文并入保留的同名章节。\n原始 TXT 不会修改，可在章节工作台撤销。";
            apply.IsEnabled = count > 0;
        }
        min.TextChanged += (_, _) => Preview(); max.TextChanged += (_, _) => Preview(); Preview();
        ignoreNumber.Click += (_, _) => Preview(); preferFirst.Click += (_, _) => Preview();
        apply.Click += (_, _) => { if (apply.IsEnabled) dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true || cleaned is null) return;
        var oldSelected = _selectedNode;
        Mutate(() => { Roots.Clear(); foreach (var root in BuildTree(cleaned)) Roots.Add(root); _selectedNode = Flatten().FirstOrDefault(n => n.Id == oldSelected?.Id)
            ?? Flatten().FirstOrDefault(n => scope?.Contains(n.Id) == true) ?? Flatten().FirstOrDefault(n => !n.IsFrontMatter); });
        SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
        SetReviewResult($"已清理 {entries.Length - cleaned.Count} 个重复成品标题，所有正文保留；原始 TXT 不变。");
    }

    private string? SuggestedTitle()
    {
        if (_selectedNode is null || _selectedNode.IsFrontMatter) return null;
        var siblings = Siblings(_selectedNode);
        var index = siblings.IndexOf(_selectedNode);
        if (index <= 0 || index + 1 >= siblings.Count) return null;
        if (siblings[index - 1].IsFrontMatter || siblings[index + 1].IsFrontMatter) return null;
        return ChapterDiagnostics.SuggestCorrectedTitle(siblings[index - 1].Title, _selectedNode.Title, siblings[index + 1].Title);
    }

    private void UpdateNumberSuggestion()
    {
        var suggestion = SuggestedTitle();
        CorrectNumberButton.Visibility = suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        CorrectNumberButton.Content = suggestion is null ? "" : "疑似错号 · 修改为：" + suggestion;
    }

    private void CorrectNumber_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged || OperationSelection().Length != 1) return;
        if (_selectedNode is not { } node || SuggestedTitle() is not { } title) return;
        Mutate(() => node.Title = title);
        UpdateSummary();
        UpdateActionButtons();
        RefreshSelectedLines();
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: ChapterTreeNode node }) return;
        e.Handled = true;
        EditTitle(node);
    }

    private void EditTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChapterTreeNode node }) EditTitle(node);
    }

    private void EditTitle(ChapterTreeNode node)
    {
        if (_sourceChanged) return;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "只修改成品正文标题与目录，原始 TXT 保持不变。", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = node.Title, Margin = new Thickness(0, 14, 0, 14) };
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存标题", IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        var dialog = new Window { Title = "编辑标题", Owner = this, Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Contains('\n') || input.Text.Contains('\r')) return;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        Mutate(() => node.Title = input.Text.Trim());
        UpdateSummary(); UpdateActionButtons();
        RefreshSelectedLines();
    }

    private void EditOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChapterTreeNode node }) return;
        if (_sourceChanged) return;
        var line = SourceLinesList.SelectedItem is ChapterTreeSourceLine chosen
            && (node.TitleLineNumber == chosen.LineNumber || node.ContentRanges.Any(r => chosen.LineNumber >= r.StartLine && chosen.LineNumber <= r.EndLine))
            ? chosen.LineNumber : node.TitleLineNumber ?? node.ContentRanges.FirstOrDefault()?.StartLine ?? 1;
        if (InkDialog.Show(this, $"将打开原始 TXT 的第 {line} 行附近。\n编辑前会创建 .bak 备份。保存原文后须重新识别章节，当前树不能直接套用。\n不支持行号定位的编辑器请使用“转到行”；选择“是”也会复制行号。", "编辑原文", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var backup = _document.SourcePath + "." + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(_document.SourcePath, backup, false);
            Clipboard.SetText(line.ToString());
            var info = CreateEditorStartInfo(TextEditorPath, _document.SourcePath, line);
            Process.Start(info)?.Dispose();
            SourceText.ToolTip = "原文备份：" + backup;
        }
        catch (Exception ex) { InkDialog.Show(this, ex.Message, "无法打开原文", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    public static ProcessStartInfo CreateEditorStartInfo(string editor, string path, int line)
    {
        editor = string.IsNullOrWhiteSpace(editor) ? "notepad.exe" : editor.Trim().Trim('"');
        var info = new ProcessStartInfo(editor) { UseShellExecute = true };
        var name = Path.GetFileNameWithoutExtension(editor).ToLowerInvariant();
        if (name == "notepad3") { info.ArgumentList.Add("/g"); info.ArgumentList.Add(line + ",1"); info.ArgumentList.Add(path); }
        else if (name == "notepad++") { info.ArgumentList.Add("-n" + line); info.ArgumentList.Add(path); }
        else if (name is "code" or "code-insiders") { info.ArgumentList.Add("--goto"); info.ArgumentList.Add(path + ":" + line + ":1"); }
        else info.ArgumentList.Add(path);
        return info;
    }
}
