using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private ReferenceLocation? _catalogLocation;
    private bool _catalogLocationLoaded;
    private string? _catalogLocationFailure;

    private void CompareDuplicateContents(ChapterReviewGroup? group)
    {
        if (_sourceChanged || group is null || group.NodeIds.Count != 2) return;
        var nodes = group.NodeIds.Select(id => Flatten().FirstOrDefault(n => n.Id == id)).OfType<ChapterTreeNode>().ToArray();
        if (nodes.Length != 2 || ChapterContentDuplicates.Compare(_document, nodes[0].ToEntry(), nodes[1].ToEntry()) is not { } pair) return;

        // 正文回答不了的问题，目录能回答：这一章本来该在哪。
        var (catalogLine, catalogText) = DescribeCatalogPosition(nodes);

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var explanation = new TextBlock
        {
            Text = (pair.Exact ? "正文完全相同（忽略空白）" : $"正文相似度 {pair.Similarity:P1} · 请仔细核对不同部分")
                + $"\n正文 {pair.FirstLines} / {pair.SecondLines} 行 · {pair.FirstCharacters} / {pair.SecondCharacters} 字符。相似度按去空白后的连续 5 字片段计算。"
                + "\n只从成品中删除选中的一章及其正文，不是合并；原始 TXT 不变，可在工作台撤销。含子章节的项不能直接删除。"
                + "\n" + catalogText,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        layout.Children.Add(explanation);
        var columns = new Grid(); Grid.SetRow(columns, 1); layout.Children.Add(columns);
        columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
        var dialog = new Window { Title = "对比重复正文 · 选择保留一份", Owner = this,
            Width = Math.Min(1100, SystemParameters.WorkArea.Width - 60), Height = Math.Min(760, SystemParameters.WorkArea.Height - 60),
            MinWidth = 640, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        string? removeId = null;
        int? navigateLine = null;
        var previews = new List<TextBox>();
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var panel = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 8, 0, i == 0 ? 8 : 0, 0) };
            Grid.SetColumn(panel, i); columns.Children.Add(panel);
            // 目录认定的那一份要看得出来是哪一份：两段正文是一样的，光看正文没人能选。
            var atCatalogPosition = catalogLine is int matched && matched == nodes[i].TitleLineNumber;
            var label = new TextBlock
            {
                Text = $"{(i == 0 ? "前一份" : "后一份")} · 原文第 {nodes[i].TitleLineNumber} 行\n{nodes[i].Title}"
                    + (atCatalogPosition ? "\n← 参考目录的位置" : ""),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            };
            DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
            var locate = new Button { Name = i == 0 ? "LocateFirstCopyButton" : "LocateSecondCopyButton",
                Content = "定位此章到章节树（不删除）", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            locate.Click += (_, _) => { navigateLine = nodes[index].TitleLineNumber; dialog.DialogResult = false; };
            DockPanel.SetDock(locate, Dock.Top); panel.Children.Add(locate);
            var button = new Button { Name = i == 0 ? "KeepFirstCopyButton" : "KeepSecondCopyButton",
                Content = i == 0 ? "保留前一份，删除后一份…" : "保留后一份，删除前一份…",
                IsEnabled = nodes[1 - i].Children.Count == 0, Margin = new Thickness(0, 12, 0, 0) };
            button.Click += (_, _) => { removeId = nodes[1 - index].Id; dialog.DialogResult = true; };
            DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
            var text = string.Join(Environment.NewLine, _document.GetSourceLines(nodes[i].ToEntry()).Select(l => $"{l.LineNumber}: {l.Text}"));
            var preview = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            previews.Add(preview); panel.Children.Add(preview);
        }
        var cancel = new Button { Content = "暂不删除", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(cancel, 2); layout.Children.Add(cancel);
        dialog.Loaded += (_, _) => { foreach (var preview in previews) { preview.Select(0, 0); preview.ScrollToHome(); } };
        var accepted = dialog.ShowDialog();
        if (navigateLine is int line) { LocateDuplicateChapter(line); return; }
        if (accepted != true || removeId is null) return;
        RemoveDuplicateCopy(removeId, nodes.Single(n => n.Id != removeId).Id);
    }

    /// <summary>
    /// What the saved directory says about which of the two copies is where.
    ///
    /// <para>This window compares prose, and prose is the one thing that <b>cannot</b> separate two copies
    /// of the same chapter — they read the same. Position separates them, and the directory is the only
    /// thing in the program that states position. Without it the reader is asked to choose between two
    /// identical stretches thousands of lines apart, which is not a choice anybody can make.</para>
    ///
    /// <para>Returns a null line and a sentence to show when there is nothing to say: no directory, no
    /// directory entry for this chapter, or a locator that could not place it. Saying so is the honest
    /// answer — a preference invented from the prose alone is exactly what this window exists to avoid.</para>
    /// </summary>
    internal (int? Line, string Text) DescribeCatalogPosition(ChapterTreeNode[] nodes)
    {
        var lines = nodes.Select(node => node.TitleLineNumber).OfType<int>().ToArray();
        if (lines.Length != 2) return (null, "两处都还没有行号，无法与参考目录核对位置。");
        if (SavedReference() is null)
            return (null, "没有参考目录：正文一样时，位置是唯一能分辨两处的东西，而位置只有目录说得清。"
                + "获取参考目录后，这里会指出哪一份在原位置。");
        if (_catalogLocationFailure is { } failure)
            return (null, "无法用参考目录核对位置：" + failure);
        if (CatalogLocation() is not { } location)
            return (null, "参考目录里没有这一章，两处都不是它的位置。");

        var hit = location.Chapters.FirstOrDefault(chapter => chapter.Lines.Any(lines.Contains));
        if (hit is null)
            return (null, "参考目录里没有这一章，两处都不是它的位置。");
        if (hit.Line is not int at)
            return (null, $"参考目录里有「{hit.Reference.Title}」，但在原文里没能定出它的位置。");

        var distance = Math.Abs(lines[0] - lines[1]);
        if (at != lines[0] && at != lines[1])
            return (null, $"参考目录把「{hit.Reference.Title}」对在第 {at} 行，不在这两处中的任何一处；"
                + "两处都不是目录的位置，请按原文前后衔接判断。");

        return (at, $"参考目录把这一章对在第 {at} 行，也就是{(at == lines[0] ? "前一份" : "后一份")}。"
            + $"两处相隔 {distance} 行、正文相同，位置是唯一能分辨它们的东西。");
    }

    /// <summary>
    /// The directory's reading of the current tree, computed once per version.
    ///
    /// <para>Locating every chapter is a pass over the whole text, and this window opens on a click, so it
    /// is cached beside the saved directory and dropped whenever that is invalidated — which is exactly
    /// when the tree or the version changed.</para>
    /// </summary>
    private ReferenceLocation? CatalogLocation()
    {
        if (_catalogLocationLoaded) return _catalogLocation;
        _catalogLocationLoaded = true;
        _catalogLocation = null;
        _catalogLocationFailure = null;
        if (SavedReference() is not { } catalog) return null;
        var lines = new string[_document.LineCount];
        for (var line = 1; line <= _document.LineCount; line++)
            lines[line - 1] = _document.SourceLine(line)?.Text ?? "";
        try { _catalogLocation = ReferenceLocator.Locate(lines, catalog); }
        catch (Exception error) { _catalogLocationFailure = error.Message; }
        return _catalogLocation;
    }

    internal void LocateDuplicateChapter(int line)
    {
        ChapterSearchText.Text = "";
        IssueCategoryCombo.SelectedIndex = 0;
        ApplySearchFilter();
        AllChapters_Click(this, new RoutedEventArgs());
        NavigateToSourceLine(line);
        ShowReviewFeedback($"已定位原文第 {line} 行的章节。已切换全部章节，可核对前后衔接；未删除内容。");
    }

    internal void RemoveDuplicateCopy(string removeId, string keepId)
    {
        if (_sourceChanged) return;
        var entries = Flatten().Select(n => n.ToEntry()).ToArray();
        var removed = entries.Single(e => e.Id == removeId);
        var kept = entries.Single(e => e.Id == keepId);
        var remaining = ChapterContentDuplicates.RemoveCopy(_document, entries, removeId, keepId);
        if (!ConfirmWorkbench($"将从成品删除原文第 {removed.TitleLineNumber} 行的“{removed.Title}”及其正文。\n保留原文第 {kept.TitleLineNumber} 行的章节。\n原始 TXT 不变，可撤销。确定删除？", "确认删除重复正文")) return;
        string? receipt;
        try
        {
            var dropped = RepairIntegrity.Coverage(entries);
            dropped.ExceptWith(RepairIntegrity.Coverage(remaining));
            RepairIntegrity.Verify(entries, remaining, dropped);
            receipt = RepairIntegrity.SaveRemovedAsync(_document, dropped.Order().ToArray()).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            SetReviewResult("未删除：备份或移除清单未完成。" + error.Message);
            return;
        }
        Mutate(() =>
        {
            Roots.Clear();
            foreach (var root in BuildTree(remaining)) Roots.Add(root);
            _selectedNode = Flatten().Single(n => n.Id == keepId);
        });
        SetOperationSelection([_selectedNode!]);
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
        SetReviewResult($"已从成品删除第 {removed.TitleLineNumber} 行的一份重复章节及正文，保留第 {kept.TitleLineNumber} 行的章节。原始 TXT 不变，可撤销。移除清单：{receipt}");
    }
}
