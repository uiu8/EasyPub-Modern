using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

internal sealed class ChapterSplitWindow : Window
{
    internal TextBox TitleInput { get; } = new() { MaxLength = 200, Margin = new Thickness(0, 8, 0, 16) };
    internal Button ConfirmButton { get; } = new() { Content = "确认切分", IsDefault = true, MinWidth = 110 };
    internal Button CancelButton { get; } = new() { Content = "取消", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
    internal string ChapterTitle => TitleInput.Text.Trim();
    internal CheckBox ExportCopyOption { get; } = new() { Content = "同时另存含新标题的 TXT 副本与章节项目", Margin = new Thickness(0, 0, 0, 8) };
    internal TextBox CatalogSearch { get; } = new() { ToolTip = "输入标题或卷名筛选参考目录", Margin = new Thickness(0, 6, 0, 6) };
    internal ComboBox CatalogTitles { get; } = new() { DisplayMemberPath = nameof(CatalogChoice.Label), MaxDropDownHeight = 240, MinWidth = 160 };
    internal Button UseCatalogTitle { get; } = new() { Content = "填入标题", IsEnabled = false, Margin = new Thickness(10, 0, 0, 0) };
    private sealed record CatalogChoice(ReferenceNode Node)
    {
        public string Label => string.IsNullOrWhiteSpace(Node.VolumeTitle) ? Node.Title : Node.VolumeTitle + " · " + Node.Title;
        public override string ToString() => Label;
    }

    internal ChapterSplitWindow(ChapterTreeDocument document, string originalTitle, ChapterContentSplit split, ReferenceCatalog? catalog = null)
    {
        Title = "从选中行建立章节";
        FontSize = 14;
        Width = 820; Height = 660; MinWidth = 640; MinHeight = 550;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        var root = new Grid { Margin = new Thickness(24) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "建立新章节", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var scope = new TextBlock { Text = "本次仅调整成品章节树 · 原始 TXT 不变 · 应用后可撤销",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) };
        header.Children.Add(scope);
        var choices = catalog?.Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter).Select(n => new CatalogChoice(n)).ToArray() ?? [];
        if (choices.Length > 0)
        {
            Height = 810; MinHeight = 720;
            header.Children.Add(new TextBlock { Text = "从参考目录选择（可选）", FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = $"依据：{catalog!.Source} · {choices.Length} 章。目录只提供标题，不能证明当前正文的切分位置。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            header.Children.Add(CatalogSearch);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(UseCatalogTitle, Dock.Right); row.Children.Add(UseCatalogTitle); row.Children.Add(CatalogTitles);
            header.Children.Add(row);
            CatalogTitles.ItemsSource = choices;
            VirtualizingPanel.SetIsVirtualizing(CatalogTitles, true);
            CatalogTitles.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            CatalogTitles.SelectionChanged += (_, _) => UseCatalogTitle.IsEnabled = CatalogTitles.SelectedItem is CatalogChoice;
            CatalogSearch.TextChanged += (_, _) =>
            {
                var query = CatalogSearch.Text.Trim();
                CatalogTitles.ItemsSource = query.Length == 0 ? choices
                    : choices.Where(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                CatalogTitles.SelectedIndex = -1;
            };
            UseCatalogTitle.Click += (_, _) =>
            {
                if (CatalogTitles.SelectedItem is CatalogChoice choice) { TitleInput.Text = choice.Node.Title; TitleInput.Focus(); }
            };
            header.Children.Add(new TextBlock { Text = "输入卷名或标题筛选，选中后点“填入标题”。仅填入下方文本，可继续修改；不会自动定位正文。",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        }
        header.Children.Add(new TextBlock { Text = "新章标题（必填）" });
        TitleInput.ToolTip = "为新章节填写成品标题，不会把选中的正文行改成标题。";
        header.Children.Add(TitleInput);
        root.Children.Add(header);
        var previews = new Grid();
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        var before = Preview("前一章 · " + originalTitle, split.Before, tail: true);
        var after = Preview("新章节 · 从原文第 " + split.After[0].StartLine + " 行开始", split.After, tail: false);
        previews.Children.Add(before); Grid.SetColumn(after, 2); previews.Children.Add(after);
        Grid.SetRow(previews, 1); root.Children.Add(previews);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(ExportCopyOption);
        var copyHint = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
            Text = "副本将在选中行之前插入上方标题，并检查能否被当前规则识别。打开生成的章节项目可保留已有移动和合并。撤销只影响当前树，导出的副本会保留。",
            Margin = new Thickness(0, 0, 0, 10) };
        ExportCopyOption.Checked += (_, _) => { copyHint.Visibility = Visibility.Visible; scope.Text = "调整成品章节树，并另存修订副本 · 原始 TXT 不变"; };
        ExportCopyOption.Unchecked += (_, _) => { copyHint.Visibility = Visibility.Collapsed; scope.Text = "本次仅调整成品章节树 · 原始 TXT 不变 · 应用后可撤销"; };
        footer.Children.Add(copyHint);
        footer.Children.Add(new TextBlock { Text = "选中行保留为新章第一行正文。这里只预览边界附近内容，不删除或改写正文。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        ConfirmButton.SetResourceReference(BackgroundProperty, "PrimaryActionBrush");
        ConfirmButton.SetResourceReference(ForegroundProperty, "PrimaryActionTextBrush");
        actions.Children.Add(CancelButton); actions.Children.Add(ConfirmButton); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        ConfirmButton.IsEnabled = false;
        TitleInput.TextChanged += (_, _) => ConfirmButton.IsEnabled = ChapterTitle.Length > 0
            && !ChapterTitle.Any(char.IsControl);
        ConfirmButton.Click += (_, _) => { if (ConfirmButton.IsEnabled) DialogResult = true; };
        CancelButton.Click += (_, _) => DialogResult = false;
        Loaded += (_, _) => TitleInput.Focus();

        Border Preview(string label, IReadOnlyList<ChapterSourceRange> ranges, bool tail)
        {
            // Only read boundary lines, even for a million-line merged chapter.
            var numbers = tail
                ? ranges.Reverse().SelectMany(r => Enumerable.Range(0, r.EndLine - r.StartLine + 1).Select(offset => r.EndLine - offset)).Take(8).Reverse()
                : ranges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1)).Take(8);
            var text = string.Join(Environment.NewLine, numbers.Select(n =>
            {
                var source = document.SourceLine(n)?.Text ?? "";
                return $"{n}  " + (source.Length > 500 ? source[..500] + "…（本行预览截断）" : source);
            }));
            var panel = new DockPanel();
            var heading = new TextBlock { Text = label + $"\n正文 {ranges.Sum(r => (long)r.EndLine - r.StartLine + 1)} 行 · " + (tail ? "末尾预览" : "开头预览"),
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 24 } });
            var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = panel };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return card;
        }
    }
}
