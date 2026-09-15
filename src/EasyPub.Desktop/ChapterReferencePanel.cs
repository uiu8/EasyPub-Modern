using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// Reference-outline driven chapter-tree adjustment: compare the book against an official directory,
/// review every difference, then apply the selected ones as one undoable chapter-tree change.
/// </summary>
public partial class ChapterEditorWindow
{
    private sealed class OutlineRow
    {
        public bool Selected { get; set; }
        public ReferenceAction Action { get; init; } = null!;
        public string Kind => Action.Kind switch
        {
            ReferenceActionKind.AddChapter => "补建章节",
            ReferenceActionKind.Retitle => "标题差异",
            ReferenceActionKind.RemoveDuplicate => "重复章节",
            ReferenceActionKind.KeepExtra => "目录外条目",
            ReferenceActionKind.DemoteExtra => "并回正文",
            _ => "说明"
        };
        public string Line => Action.Line > 0 ? Action.Line.ToString() : "";
        public string Title => Action.Title;
        public string Detail => Action.Detail;
    }

    private void ReferenceOutlineAssist(ReferenceCatalog? catalog, string fallbackTitles)
    {
        if (_sourceChanged) return;
        catalog ??= CatalogFromText(fallbackTitles);
        if (catalog is null)
        {
            SetReviewResult("需要先获取参考目录，或在目录文字中粘贴/导入至少 3 行目录。");
            return;
        }
        var snapshot = Flatten().Select(node => node.ToEntry()).ToArray();
        var plan = ReferencePlanner.Build(_document, snapshot, catalog);
        if (plan.Actions.Count == 0)
        {
            SetReviewResult("参考目录与当前章节树一致，没有需要调整的项目。");
            return;
        }

        var dialog = ThemedWindow("依据参考目录调整章节树", 1080, 760);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "依据参考目录调整章节树", FontSize = 22, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = $"参考目录：{catalog.PageTitle}（{catalog.VolumeTitles.Count} 卷 · {catalog.Titles.Count} 章）\n" +
                   $"补建 {plan.AddCount} · 标题差异 {plan.RetitleCount} · 重复章节 {plan.DuplicateCount} · 目录外条目 {plan.ExtraCount}\n" +
                   "原始 TXT 不变；去重会从成品移除所选正文，应用前备份并保存完整移除清单，可撤销。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6)
        });
        var options = new WrapPanel(); top.Children.Add(options);
        var useReferenceTitles = new CheckBox
        {
            Content = "使用参考目录标题（否则保留原文标题，可用于保留内嵌卷名）",
            IsChecked = true, VerticalAlignment = VerticalAlignment.Center
        };
        options.Children.Add(useReferenceTitles);
        var onlyDuplicates = new CheckBox { Content = "只看去重项", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        options.Children.Add(onlyDuplicates);
        var volumeLevels = new CheckBox
        {
            Content = $"建立卷层级（{catalog.VolumeTitles.Count} 卷）",
            IsChecked = catalog.VolumeTitles.Count > 1,
            IsEnabled = catalog.VolumeTitles.Count > 1,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
            ToolTip = "把参考目录的卷作为一级目录，其下章节作为二级目录。卷与其第一章共用原文标题行，不新增任何正文行。"
        };
        options.Children.Add(volumeLevels);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; top.Children.Add(status);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "应用所选调整" };
        var cancel = new Button { Content = "关闭", IsCancel = true };
        footer.Children.Add(apply); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var evidence = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, Height = 145,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
            Text = "选择一项查看原文；重复项同时显示候选位置，正文不同时请核对后再勾选。",
            Margin = new Thickness(0, 8, 0, 8)
        };
        DockPanel.SetDock(evidence, Dock.Bottom); panel.Children.Add(evidence);

        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "应用", Binding = new System.Windows.Data.Binding("Selected") });
        foreach (var (header, binding, width) in new[]
                 {
                     ("类型", "Kind", 90.0), ("原文行", "Line", 70.0),
                     ("标题", "Title", 260.0), ("说明", "Detail", 0.0)
                 })
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(binding), IsReadOnly = true,
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star)
            });
        panel.Children.Add(grid); dialog.Content = panel;
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not OutlineRow row) return;
            var positions = row.Action.Kind == ReferenceActionKind.RemoveDuplicate
                ? plan.Location.Chapters.FirstOrDefault(c => ReferenceEquals(c.Reference, row.Action.Reference))?.Lines ?? []
                : row.Action.Line > 0 ? new[] { row.Action.Line } : [];
            evidence.Text = row.Detail + Environment.NewLine + string.Join(Environment.NewLine,
                positions.Take(4).Select(line => $"—— 原文第 {line} 行起（最多显示 16 行）——" + Environment.NewLine +
                    string.Join(Environment.NewLine, Enumerable.Range(line, Math.Min(16, _document.LineCount - line + 1))
                        .Select(n => $"{n}  {_document.SourceLine(n)?.Text}"))));
        };

        var rows = plan.Actions
            .Select(action => new OutlineRow { Action = action, Selected = plan.DefaultSelection.Contains(action) })
            .ToArray();
        void Refresh()
        {
            grid.ItemsSource = onlyDuplicates.IsChecked == true
                ? rows.Where(row => row.Action.Kind == ReferenceActionKind.RemoveDuplicate).ToArray()
                : rows;
            var count = rows.Count(row => row.Selected);
            status.Text = $"已勾选 {count} 项。默认勾选补建、非手改标题、疑似正文标题及正文一致的去重项；可取消勾选。手工结构保留，应用前显示实际变化。";
        }
        onlyDuplicates.Checked += (_, _) => Refresh();
        onlyDuplicates.Unchecked += (_, _) => Refresh();
        Refresh();

        apply.Click += async (_, _) =>
        {
            grid.CommitEdit(); grid.CommitEdit();
            var selected = rows.Where(row => row.Selected).Select(row => row.Action).ToArray();
            if (selected.Length == 0 && volumeLevels.IsChecked != true) { status.Text = "请至少勾选一项。"; return; }
            try
            {
                var dropped = new List<int>();
                var rebuilt = ReferencePlanner.Apply(_document, snapshot, plan, selected, useReferenceTitles.IsChecked == true, volumeLevels.IsChecked == true, dropped);
                if (!ConfirmRepairChanges(snapshot, rebuilt)) return;
                SourceBackupStore.CreateDefault().EnsureSnapshot(_document);
                var receipt = await RepairIntegrity.SaveRemovedAsync(_document, dropped);
                ReferenceCatalogInput.SaveCatalog(_document.SourceSha256, catalog);
                InvalidateBreakpoints();
                Mutate(() =>
                {
                    Roots.Clear();
                    foreach (var root in BuildTree(rebuilt)) Roots.Add(root);
                    _selectedNode = Flatten().FirstOrDefault();
                }, markManual: false);
                SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
                RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
                dialog.DialogResult = true;
                SetReviewResult($"已依据参考目录调整章节树（{selected.Length} 项），原始 TXT 不变，可撤销。{(receipt is null ? "" : " 移除清单：" + receipt)}");
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        dialog.ShowDialog();
    }

    /// <summary>Builds a chapter-only catalog from pasted or imported directory text.</summary>
    private static ReferenceCatalog? CatalogFromText(string text)
    {
        return ReferenceCatalogInput.ParseText(text);
    }

    private bool ConfirmRepairChanges(IReadOnlyList<ChapterTreeEntry> before, IReadOnlyList<ChapterTreeEntry> after)
    {
        var dialog = ThemedWindow("确认实际变化", 900, 650);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "应用以上变化" };
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = "返回核对", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "原始 TXT 不变；应用前备份，移除正文会生成逐行清单。手工章节的顺序、层级及正文范围保留。\n\n"
                + string.Join(Environment.NewLine, RepairIntegrity.DescribeChanges(before, after)) });
        dialog.Content = panel; apply.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true;
    }
}
