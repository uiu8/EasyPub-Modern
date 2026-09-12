using System.Windows.Input;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private ChapterTreeNode? _selectionAnchor;
    private bool _applyingMultiSelection;
    private bool _hasOperationSelection;

    private ChapterTreeNode[] OperationSelection() => _hasOperationSelection
        ? Flatten().Where(n => n.IsOperationSelected).ToArray()
        : _selectedNode is null ? [] : [_selectedNode];

    private void SetOperationSelection(IEnumerable<ChapterTreeNode> selection)
    {
        var set = selection.ToHashSet();
        foreach (var node in Flatten()) node.IsOperationSelected = set.Contains(node);
        _hasOperationSelection = true;
        BatchSelectionText.Text = $"已选 {set.Count} 章 · Ctrl 点选，Shift 连选 · 左侧勾选框仅控制目录";
    }

    public void SelectChapterForOperation(ChapterTreeNode node, ModifierKeys modifiers)
    {
        ClearReviewResult();
        _expandSource = false;
        var control = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var selected = control ? OperationSelection().ToHashSet() : [];
        if (shift && _selectionAnchor is not null)
        {
            var visible = Flatten().Where(n => n.IsReviewVisible && AncestorsExpanded(n)).ToList();
            var start = visible.IndexOf(_selectionAnchor);
            var end = visible.IndexOf(node);
            if (start >= 0 && end >= 0)
                foreach (var item in visible.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1)) selected.Add(item);
            else selected.Add(node);
        }
        else
        {
            if (!control || !selected.Remove(node)) selected.Add(node);
            _selectionAnchor = node;
        }
        SetOperationSelection(selected);
        _selectedNode = node;
        _applyingMultiSelection = true;
        try { SelectRestoredNode(); }
        finally { _applyingMultiSelection = false; }
        RefreshSelectedLines(); UpdateActionButtons();
    }

    private static bool AncestorsExpanded(ChapterTreeNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (!parent.IsExpanded) return false;
        return true;
    }

    private static IEnumerable<ChapterSourceRange> WithOriginalTitle(ChapterTreeNode node)
    {
        if (node.TitleLineNumber is int line && !node.ContentRanges.Any(r => r.StartLine <= line && r.EndLine >= line))
            yield return new(line, line);
        foreach (var range in node.ContentRanges) yield return range;
    }

    private void UpdateBatchActionButtons()
    {
        var count = OperationSelection().Length;
        BatchSelectionText.Text = $"已选 {count} 章 · {PlanBatchAction(OperationSelection(), true).Count} 个连续段 · Ctrl 多选 / Shift 连选";
        MergeButton.Content = count > 1 ? $"还原 {count} 章为正文" : "还原为上一章正文";
        DemoteButton.Content = count > 1 ? $"设为子章节（{count} 章）" : "设为上一章的子章节";
        if (count == 1) return;
        MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = PromoteButton.IsEnabled = SplitButton.IsEnabled = false;
        MergeButton.IsEnabled = !_sourceChanged && PlanBatchAction(OperationSelection(), true).Any(g => g.Reason is null);
        DemoteButton.IsEnabled = !_sourceChanged && PlanBatchAction(OperationSelection(), false).Any(g => g.Reason is null);
    }

    private IReadOnlyList<ChapterBatchGroup> PlanBatchAction(IEnumerable<ChapterTreeNode> selection, bool merge)
    {
        var selected = selection.ToHashSet();
        // Selected parents own their subtrees; do not process their selected descendants twice.
        selected.RemoveWhere(node => HasSelectedAncestor(node, selected));
        var groups = new List<ChapterBatchGroup>();
        foreach (var siblings in new[] { Roots }.Concat(Flatten().Select(n => n.Children)))
        {
            var snapshot = siblings.ToArray();
            for (var i = 0; i < snapshot.Length; i++)
            {
                if (!selected.Contains(snapshot[i])) continue;
                var start = i;
                while (i + 1 < snapshot.Length && selected.Contains(snapshot[i + 1])) i++;
                var target = start > 0 ? snapshot[start - 1] : null;
                var nodes = snapshot[start..(i + 1)];
                var reason = target is null ? "没有同父级的上一章" : target.IsFrontMatter || nodes.Any(n => n.IsFrontMatter) ? "前置项不参与归并"
                    : merge && (target.Children.Count > 0 || nodes.Any(n => n.Children.Count > 0)) ? "含有子章节，请先调整层级"
                    : !merge && nodes.Any(n => MaxRelativeDepth(n) + target.Level > 4) ? "调整后超过四级目录" : null;
                groups.Add(new(target, nodes, reason));
            }
        }
        return groups;
    }

    private static string DescribeBatchPlan(IReadOnlyList<ChapterBatchGroup> groups) => string.Join("\n", groups.Take(4).Select(g =>
        g.Reason is null ? $"{g.Nodes.Length} 处 → {g.Target!.Title}" : $"跳过「{g.Nodes[0].Title}」：{g.Reason}"))
        + (groups.Count > 4 ? $"\n另有 {groups.Count - 4} 段；执行前可查看完整影响范围。" : "");

    private bool TryBatchChapterAction(bool merge)
    {
        if (_sourceChanged) return true;
        var selection = OperationSelection();
        if (selection.Length <= 1) return false;
        var groups = PlanBatchAction(selection, merge);
        var valid = groups.Where(g => g.Reason is null).ToArray();
        if (valid.Length == 0) { ShowReviewFeedback(DescribeBatchPlan(groups)); return true; }
        if ((valid.Length != groups.Count || groups.Count > 4) && !ConfirmWorkbench(
            $"可处理 {valid.Sum(g => g.Nodes.Length)} 章，跳过 {groups.Count - valid.Length} 组。\n\n"
            + string.Join("\n", groups.Select(g => g.Reason is null ? $"{g.Nodes.Length} 章 → {g.Target!.Title}" : $"跳过「{g.Nodes[0].Title}」：{g.Reason}"))
            + "\n\n是否仅处理上述可处理项？", "确认批量处理")) return true;
        if (valid.Length > 0)
        {
            Mutate(() =>
            {
                foreach (var (target, nodes, _) in valid)
                {
                    foreach (var node in nodes)
                    {
                        Siblings(node).Remove(node);
                        if (merge) target!.ContentRanges = target.ContentRanges.Concat(WithOriginalTitle(node)).ToArray();
                        else { target!.Children.Add(node); node.Parent = target; SetLevelRecursive(node, target.Level + 1); }
                    }
                    target!.NotifyLineCount();
                }
            });
            _selectedNode = valid[0].Target;
            SetOperationSelection([_selectedNode!]);
            SelectRestoredNode();
            RefreshSelectedLines(); UpdateActionButtons();
        }
        BatchSelectionText.Text = $"已处理 {valid.Sum(g => g.Nodes.Length)} 章；跳过 {groups.Count - valid.Length} 组（无可用上一章、含子章节或超出层级限制） · 可撤销";
        SetReviewResult((merge ? $"已将 {valid.Sum(g => g.Nodes.Length)} 处还原为正文，原始标题行和正文全部保留。" : $"已将 {valid.Sum(g => g.Nodes.Length)} 章设为子章节，章节身份与正文均保留。")
            + $" 跳过 {groups.Count - valid.Length} 组。"
            + string.Join("；", groups.Where(g => g.Reason is not null).Select(g => $"「{g.Nodes[0].Title}」：{g.Reason}")));
        return true;
    }

    private static bool HasSelectedAncestor(ChapterTreeNode node, HashSet<ChapterTreeNode> selected)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (selected.Contains(parent)) return true;
        return false;
    }
}

internal sealed record ChapterBatchGroup(ChapterTreeNode? Target, ChapterTreeNode[] Nodes, string? Reason);
