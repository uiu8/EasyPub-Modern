namespace EasyPub.Core;

/// <summary>
/// The repair plan a book can produce from itself, when there is no directory to align it against.
///
/// <para>Measured boundary this exists to close: with no catalog the one repair entry returns
/// <c>Plan = null</c>, and both landing modes answer "nothing to apply". But the tree does show problems
/// without any outside help — on the six-volume sample there are 32 groups of repeated titles covering 64
/// entries, five chapter-number restarts and 49 missing numbers. So "no catalog means nothing can be done"
/// was never a capability limit; it was a missing <b>source of judgement</b>.</para>
///
/// <para>This supplies that source, and it is deliberately narrow. It only raises actions whose verdict
/// follows from the book alone:</para>
///
/// <list type="bullet">
/// <item>A title that appears more than once — one copy is what the book meant, and which one to keep
/// follows from position rather than from any directory.</item>
/// <item>A title whose numbering is not written the canonical way, where the canonical form can be derived
/// from the title itself (<c>第1章</c> → <c>第一章</c>).</item>
/// </list>
///
/// <para>Everything a directory is needed to decide stays out: whether a chapter is <i>missing</i>, whether
/// a heading should be <i>adopted</i> from body text, whether an extra chapter is really extra. Guessing at
/// those from the book alone is how a repair invents chapters, and the rule here is that it never does.</para>
///
/// <para>It raises the same <see cref="ReferenceActionKind"/> values the catalog path does, and no new ones.
/// That is what lets a self-repair reuse the whole chain unchanged: the same effect compiler, the same plan
/// compiler, the same transaction, the same landing modes. A new action kind would have needed a new branch
/// in every one of them, and each branch would have been a place for the two paths to drift apart.</para>
/// </summary>
public static class SelfRepairPlanner
{
    /// <summary>The basis name a self-repair proposal carries, so a receipt says where the plan came from.</summary>
    public const string BasisName = "SelfRepair";

    /// <summary>What the synthesized catalog calls itself, so a reader can see it was not downloaded.</summary>
    public const string SourceName = "本书自身（未使用参考目录）";

    public static ReferencePlan Build(ChapterTreeDocument document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entries = document.Entries;
        var actions = new List<ReferenceAction>();
        var located = new List<LocatedChapter>();
        var nodes = new List<ReferenceNode>();

        // 1. Repeated titles. One copy of a repeated title is the chapter; the others are copies of it.
        //
        //    Which one to keep is decided by position — the first occurrence — and not by any directory:
        //    a directory would say which position it agrees with, and there is none. Anchoring the action
        //    at the later copy is what makes the default selection ("drop the duplicate from the finished
        //    book") keep the copy the reader sees first.
        foreach (var group in entries
                     .Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is not null)
                     .GroupBy(entry => entry.Title, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Min(entry => entry.TitleLineNumber!.Value)))
        {
            token.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(entry => entry.TitleLineNumber).ToArray();
            var keep = ordered[0];
            var node = new ReferenceNode(group.Key, ReferenceNodeKind.Chapter, null, null);
            nodes.Add(node);
            // Lines lists every copy, not only the one being kept. The effect compiler works out which lines
            // a repair takes out of the book by subtracting the action's own line from this list — so a list
            // holding only the kept copy would make the deleted copy look like a line to keep.
            located.Add(new LocatedChapter(node, null, keep.TitleLineNumber, keep.Title,
                ordered.Select(entry => entry.TitleLineNumber!.Value).ToArray()));

            foreach (var copy in ordered.Skip(1))
                actions.Add(new ReferenceAction(
                    ReferenceActionKind.RemoveDuplicate,
                    // Anchored at the copy that STAYS, not at the one that goes. The effect compiler works out
                    // which lines leave the book by subtracting the action's own line from the located copies,
                    // so an action anchored at the deleted copy would compute the kept copy as the one to
                    // remove — and the repair would delete the wrong line.
                    keep.TitleLineNumber!.Value,
                    copy.Title,
                    $"「{copy.Title}」在原文中出现 {ordered.Length} 次；保留第 {keep.TitleLineNumber} 行那一份，"
                    + $"其余 {ordered.Length - 1} 处（第 "
                    + string.Join("、", ordered.Skip(1).Select(entry => entry.TitleLineNumber!.Value))
                    + " 行）默认只从成品里排除。选「同时改原文」并勾上「并从原文删除这一份」时，它们会从 TXT 里删掉。",
                    copy.Id,
                    node,
                    // Recommended. A title printed twice in a row is not a judgement call: the book meant one
                    // chapter, and which copy to keep follows from position rather than from taste. Leaving it
                    // unticked would make "no catalog, edit the source" a mode that offers nothing to do.
                    Recommended: true));
        }

        // 2. Titles whose numbering is not written the canonical way. The corrected form is derived from
        //    the title itself, so no directory is consulted — and when it cannot be derived, nothing is
        //    raised rather than a guess being made.
        foreach (var entry in entries.Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is not null))
        {
            token.ThrowIfCancellationRequested();
            if (!ChapterTitleNormalizer.TryNormalizeNumericTitle(entry.Title, out var normalized)) continue;
            if (string.Equals(normalized, entry.Title, StringComparison.Ordinal)) continue;

            var node = new ReferenceNode(normalized, ReferenceNodeKind.Chapter, null, null);
            nodes.Add(node);
            located.Add(new LocatedChapter(node, null, entry.TitleLineNumber, entry.Title,
                [entry.TitleLineNumber!.Value]));

            actions.Add(new ReferenceAction(
                ReferenceActionKind.Retitle,
                entry.TitleLineNumber!.Value,
                entry.Title,
                $"标题「{entry.Title}」的编号写法不规范，可改为「{normalized}」。",
                entry.Id,
                node,
                // Recommended: the corrected form follows from the title itself, so there is nothing for a
                // reader to weigh up. This is also the action that gives "no catalog, edit the source"
                // something real to do.
                Recommended: true));
        }

        var catalog = new ReferenceCatalog(SourceName, document.SourcePath, nodes);
        return new ReferencePlan(catalog, new ReferenceLocation(located), actions);
    }
}
