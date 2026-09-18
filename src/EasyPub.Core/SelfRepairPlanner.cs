namespace EasyPub.Core;

/// <summary>
/// 两个 occurrence 是不是**同一个逻辑章节** —— 只回答这一个问题。
///
/// <para>三档都**不**回答"该留哪一份"，也**不**回答"能不能删"。那两个问题在文档里是独立维度
/// （`PreferredKeepId` / `TreeRemovalEligibility`），把它们和身份压成一个置信度，
/// 正是这一层此前会把"正文相同"直接推成"默认删后一份"的原因。</para>
/// </summary>
public enum DuplicateIdentity
{
    /// <summary>仅标题相同。只说明"值得看一眼"，不足以生成删除动作。</summary>
    Candidate,

    /// <summary>正文相似（按 <see cref="ChapterContentDuplicates"/> 的口径）。</summary>
    Probable,

    /// <summary>正文完全相同。**仍然只说明两份内容等价，不说明该留哪一份。**</summary>
    Confirmed,
}

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
/// follows from <b>structure or body evidence</b>, not from position (see below).</item>
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
/// compiler, the same transaction, the same landing modes.</para>
///
/// <para><b>0-A 修正后的重复证据模型</b>（docs/2026-09-18_交互设计与情境决策映射.md §2.2.1）。</para>
///
/// <para>原实现只做 <c>GroupBy(Title)</c>，然后"保留第一份、其余全部 <c>Recommended = true</c>"。
/// 它有两个已被测试复现的缺陷（夹具见 <c>DuplicateEvidenceTests</c> 的 A / B / C / D1）：</para>
///
/// <list type="number">
/// <item><b>跨卷与章号重启的同名章被合并。</b>把 <c>Parent + Level</c> 加进分组键能拦住
/// <i>显式分卷</i>的书，但拦不住六卷样书那种"原文一个卷标题行都没有"的书 —— 它的所有条目父都是空，
/// 于是结构键退化成 <c>Level + Title</c>，跨卷同名照样被合并。<b>真正能拦住它的是正文证据。</b></item>
/// <item><b>"同标题"被直接当成"副本"。</b>正文完全不同、或正文相同但无法判断该留哪一份时，
/// 都被默认推荐删除。</item>
/// </list>
///
/// <para>新模型把三件事分开：</para>
///
/// <list type="bullet">
/// <item><b>Identity</b> —— 正文证据，复用 <see cref="ChapterContentDuplicates"/>，不新写比较算法。</item>
/// <item><b>PreferredKeep</b> —— 本轮只承认一种证据：<b>结构事实</b>（同名组里恰好一份有自己的正文，
/// 其余是空章）。"先出现的赢"（<c>FirstWins</c>）不是证据，所以它不再让 <c>Recommended</c> 成立。</item>
/// <item><b>RemovalSafe</b> —— 待删的那一份是叶子，且不是用户手工建立的。</item>
/// </list>
///
/// <para><b>"产生动作"与"默认勾选"是两件事。</b>身份达到门槛、或存在结构证据时，动作仍然产生 ——
/// 用户能在清单里看到它并自己决定；但 <c>Recommended</c> 只在上面三者同时成立时才为 true。
/// 这样"无目录 + 改原文"不会变成一个无事可做的模式，同时软件也不替用户做危险的删除决定。</para>
/// </summary>
public static class SelfRepairPlanner
{
    /// <summary>The basis name a self-repair proposal carries, so a receipt says where the plan came from.</summary>
    public const string BasisName = "SelfRepair";

    /// <summary>
    /// What the synthesized catalog calls itself.
    ///
    /// <para>注意这句话只承诺一件事：**本次 planner 不重新读取 ReferenceCatalog**。它**不**承诺
    /// "当前输入树从未受过目录影响" —— 输入树可能已经被 <c>ReferencePlanner</c> 校验过、被用户手工改过，
    /// 或经过版本迁移。UI 上的对应说法是「基于当前章节树检查」。</para>
    /// </summary>
    public const string SourceName = "本书自身（未使用参考目录）";

    public static ReferencePlan Build(ChapterTreeDocument document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entries = document.Entries;
        var actions = new List<ReferenceAction>();
        var located = new List<LocatedChapter>();
        var nodes = new List<ReferenceNode>();

        // 叶子判定要在**完整条目序列**上做：子章节不一定与父同组，只看组内会把它当成叶子。
        var isLeaf = new Dictionary<string, bool>();
        for (var i = 0; i < entries.Count; i++)
            isLeaf[entries[i].Id] = i == entries.Count - 1 || entries[i + 1].Level <= entries[i].Level;

        var parents = BuildParentMap(entries);

        // 1. Repeated titles —— 证据模型见类注释。
        foreach (var group in entries
                     .Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is not null)
                     .GroupBy(entry => (Parent: parents[entry.Id], entry.Level, entry.Title))
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Min(entry => entry.TitleLineNumber!.Value))
                     .ToArray())
        {
            token.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(entry => entry.TitleLineNumber).ToArray();

            // PreferredKeep：本轮唯一承认的证据是**结构事实** —— 恰好一份有自己的正文，其余是空章。
            // 两份都有正文时，没有任何本地证据说明该留哪一份，于是留空，由用户决定。
            var withBody = ordered.Where(HasOwnBody).ToArray();
            var keep = withBody.Length == 1 ? withBody[0] : null;

            var identity = AssessIdentity(document, ordered, token);

            // 既没有结构证据、身份也没达到门槛 —— 只是标题撞了，不足以生成动作。
            if (keep is null && identity == DuplicateIdentity.Candidate) continue;

            // 锚点必须落在**保留**的那一份上：效果编译器用"定位到的所有副本减去动作自己那一行"
            // 来算哪些行离开这本书，锚在被删行上会算反，删掉留下的那一份。
            var anchor = keep ?? ordered[0];
            var node = new ReferenceNode(anchor.Title, ReferenceNodeKind.Chapter, null, null);
            nodes.Add(node);
            // Lines 列出**每一份副本**，不只是保留的那一份 —— 编译器靠它反推被移除的行。
            located.Add(new LocatedChapter(node, null, anchor.TitleLineNumber, anchor.Title,
                ordered.Select(entry => entry.TitleLineNumber!.Value).ToArray()));

            foreach (var copy in ordered.Where(entry => !ReferenceEquals(entry, anchor)))
            {
                var safe = keep is not null && isLeaf[copy.Id] && copy.RecognitionSource != "manual";
                actions.Add(new ReferenceAction(
                    ReferenceActionKind.RemoveDuplicate,
                    anchor.TitleLineNumber!.Value,
                    copy.Title,
                    DescribeDuplicate(ordered, anchor, copy, identity, keep is not null, safe),
                    copy.Id,
                    node,
                    Recommended: safe));
            }
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

    /// <summary>
    /// 父节点推导 —— 与 <c>ChapterReviewAnalyzer</c> 用同一套栈式规则，避免两处对"父"的理解不一致。
    /// </summary>
    private static Dictionary<string, string> BuildParentMap(IReadOnlyList<ChapterTreeEntry> entries)
    {
        var parents = new Dictionary<string, string>();
        var stack = new Stack<ChapterTreeEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsFrontMatter) { stack.Clear(); parents[entry.Id] = ""; continue; }
            while (stack.Count > 0 && stack.Peek().Level >= entry.Level) stack.Pop();
            parents[entry.Id] = stack.Count > 0 ? stack.Peek().Id : "";
            stack.Push(entry);
        }
        return parents;
    }

    /// <summary>这一章是否带着自己的正文行。空章与"有正文的章"是结构上不同的事实。</summary>
    private static bool HasOwnBody(ChapterTreeEntry entry) =>
        entry.ContentRanges.Sum(range => range.EndLine - range.StartLine + 1) > 0;

    /// <summary>
    /// 身份置信度 —— 复用 <see cref="ChapterContentDuplicates"/>，**不重新实现一遍正文比较**。
    ///
    /// <para>那个比较器自带两道门槛（最短正文 20 字符、相似度 0.90），它们专门避免"很短的模板文字"
    /// 被当成强重复证据。这里只负责把它的结论映射成三档，不越过它自己判断。</para>
    /// </summary>
    private static DuplicateIdentity AssessIdentity(
        ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry> ordered, CancellationToken token)
    {
        var best = DuplicateIdentity.Candidate;
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                token.ThrowIfCancellationRequested();
                if (ChapterContentDuplicates.Compare(document, ordered[i], ordered[j]) is not { } match) continue;
                if (match.Exact) return DuplicateIdentity.Confirmed;
                best = DuplicateIdentity.Probable;
            }
        }
        return best;
    }

    private static string DescribeDuplicate(IReadOnlyList<ChapterTreeEntry> ordered, ChapterTreeEntry anchor,
        ChapterTreeEntry copy, DuplicateIdentity identity, bool hasKeepTarget, bool recommended)
    {
        var lines = string.Join("、", ordered.Select(entry => entry.TitleLineNumber!.Value));
        var where = $"「{copy.Title}」在原文中出现 {ordered.Count} 次（第 {lines} 行）。";

        if (recommended)
            return where + $"第 {anchor.TitleLineNumber} 行那一份带着正文，其余是空章；"
                + $"保留它、移除第 {copy.TitleLineNumber} 行这一份。"
                + "选「同时改原文」并勾上「并从原文删除这一份」时，它才会从 TXT 里删掉。";

        var why = identity switch
        {
            DuplicateIdentity.Confirmed => "两份正文完全相同，但**没有证据说明该留哪一份**（位置先后不算证据）。",
            DuplicateIdentity.Probable => "两份正文相似，但**没有证据说明该留哪一份**。",
            _ => "标题相同，正文内容并不相同 —— 可能只是同名的两章。",
        };
        var tail = hasKeepTarget
            ? "该份不是叶子节点或属于手工修改，需要你自己核对。"
            : "请对比后自己决定保留哪一份；默认不会替你删除。";
        return where + why + tail;
    }
}
