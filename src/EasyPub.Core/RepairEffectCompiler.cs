using System.Globalization;

namespace EasyPub.Core;

/// <summary>
/// Says what each action does, in the three dimensions source editing requires.
///
/// <para>Where the information to answer all three is not in hand, the effect says so —
/// <c>Indeterminate</c> — rather than guessing. That is deliberate and testable: an action whose effect
/// cannot be stated is refused in <c>EditSource</c> while still working in <c>TreeOnly</c>. Guessing here
/// is how a plan ends up promising one thing in the preview and doing another when applied.</para>
///
/// <para>Actions that only report — a chapter whose body is missing, a directory entry that was never
/// located — have no effect at all: not "unchanged", but nothing to do. They are still described, because
/// the window shows them and a row with no description would have to be special-cased there instead.</para>
/// </summary>
public static class RepairEffectCompiler
{
    /// <summary>
    /// The effect of one action, as far as it can be stated.
    ///
    /// <paramref name="chapterNumber"/> is the chapter number the reference gives this action, when it has
    /// one; it is what a heading inserted into the text would say. When the directory does not number the
    /// entry there is no heading to write, so the action cannot be performed on the text at all.
    /// </summary>
    public static RepairEffect Describe(ReferenceAction action, int? chapterNumber = null,
        BodyAssignment? ownership = null, Func<int, string?>? lineText = null)
    {
        var title = action.Title ?? "";
        switch (action.Kind)
        {
            // 参考目录有、章节树没有，而原文里标题行已经定位到。收录：只改树，不动一个字。
            case ReferenceActionKind.AddChapter:
                return new RepairEffect(
                    new TreeEffectInsertEntry(title, 1),
                    new SourceContentUnchanged(),
                    new BodyOwnershipUnchanged());

            // 标题与目录写法不同。改树上的标题，并把原文那一行换掉。
            //
            // 前置条件用**原文那一行现在的样子**，不是动作记着的标题：动作可能是在别的行上算出来的，
            // 而这一行必须有它自己当时的内容才算数。取不到就把这一维标成不确定 —— 拿动作标题去当
            // 前置条件，会让"这一行已经不是原来的内容"这种检查形同虚设。
            case ReferenceActionKind.Retitle:
            {
                if (action.Line < 1) return Indeterminate("没有定位到标题行，无法说明要改哪一行");
                var expected = lineText?.Invoke(action.Line) ?? action.Title ?? "";
                return new RepairEffect(
                    new TreeEffectRetitleEntry(title),
                    new SourceContentReplace([new ReplaceIntent(action.Line, expected, title)]),
                    new BodyOwnershipUnchanged());
            }

            // 原文里这一段没有标题行，只有正文。要不要把它立成标题，由人看过再决定。
            // 立标题本身不动文本（那一行文字保留），所以文本内容不变；但正文归属会变：
            // 立成标题之后，它下面那段文字从此归它。
            case ReferenceActionKind.AdoptHeading:
                if (ownership is null)
                    return Indeterminate("还不知道立起标题后哪一段文字归它，无法说明正文归属");
                return new RepairEffect(
                    new TreeEffectInsertEntry(title, 1),
                    new SourceContentUnchanged(),
                    new BodyOwnershipAssigned(ownership)) { Ownership = ownership };

            // 同一章出现了多次。默认只从成品里排除多余的那份 —— 树变了，文本一个字没动。
            case ReferenceActionKind.RemoveDuplicate:
                return new RepairEffect(
                    new TreeEffectRemoveEntry(),
                    new SourceContentUnchanged(),
                    ownership is null
                        ? new BodyOwnershipIndeterminate()
                        : new BodyOwnershipAssigned(ownership)) { Ownership = ownership };

            // 目录里没有这一条。默认保留，用户明确勾选才并回正文；并回只是取消边界，
            // 那一行文字本来就在正文流里。
            case ReferenceActionKind.DemoteExtra:
                return new RepairEffect(
                    new TreeEffectRemoveEntry(),
                    new SourceContentUnchanged(),
                    ownership is null
                        ? new BodyOwnershipIndeterminate()
                        : new BodyOwnershipAssigned(ownership)) { Ownership = ownership };

            // 只报告的发现项：没有可执行的效果。
            case ReferenceActionKind.MissingBody:
            case ReferenceActionKind.KeepExtra:
            case ReferenceActionKind.VolumeNote:
                return new RepairEffect(
                    new TreeEffectUnchanged(),
                    new SourceContentUnchanged(),
                    new BodyOwnershipUnchanged());

            default:
                return Indeterminate("这个动作的效果还没有定义");
        }
    }

    /// <summary>
    /// The effect of removing a duplicate copy <b>and deleting it from the TXT</b>.
    ///
    /// <para>Separate from <see cref="Describe"/> because it is not a stronger version of the same
    /// action — it is a different answer to a question the user was asked. The lines to delete have to be
    /// named, so this cannot be inferred; it comes from the located duplicate's line span.</para>
    /// </summary>
    public static RepairEffect DescribeDuplicateDeletion(ReferenceAction action, IReadOnlyList<int> lines)
    {
        if (lines.Count == 0)
            return Indeterminate("没有可删除的行，无法说明要删哪几行");
        var deletions = new List<SourceLineSpan>();
        var ordered = lines.Distinct().Order().ToArray();
        var start = ordered[0];
        var end = start;
        foreach (var line in ordered.Skip(1))
        {
            if (line == end + 1) { end = line; continue; }
            deletions.Add(new SourceLineSpan(start, end + 1));
            start = line;
            end = line;
        }
        deletions.Add(new SourceLineSpan(start, end + 1));
        return new RepairEffect(
            new TreeEffectRemoveEntry(),
            new SourceContentDelete(deletions),
            new BodyOwnershipAssigned(BodyAssignment.Empty)) { Ownership = BodyAssignment.Empty };
    }

    /// <summary>Every effect an action could have, for the table the confirmation window shows.</summary>
    public static IReadOnlyList<RepairEffect> DescribeAll(RepairProposal proposal,
        Func<int, string?>? lineText = null)
    {
        var effects = new List<RepairEffect>(proposal.Actions.Count);
        foreach (var action in proposal.Actions)
            effects.Add(Describe(action, ChapterNumberOf(action), lineText: lineText));
        return effects;
    }

    /// <summary>
    /// The chapter number the reference gives an action, if it gives one. Read from the reference title
    /// rather than the local one: the local title is what is being corrected.
    /// </summary>
    public static int? ChapterNumberOf(ReferenceAction action)
    {
        var source = action.Reference?.Title ?? action.Title;
        if (string.IsNullOrEmpty(source)) return null;
        var key = ReferenceOutline.ParseKey(source);
        if (key.Number.Length == 0) return null;
        return int.TryParse(key.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Refuses an action outright, in the dimension that could not be stated. Used when the effect cannot
    /// be described at all rather than a particular dimension being unknown.
    /// </summary>
    private static RepairEffect Indeterminate(string _) => new(
        new TreeEffectIndeterminate(),
        new SourceContentIndeterminate(),
        new BodyOwnershipIndeterminate());

    /// <summary>
    /// The heading text a chapter inserted into the text would carry. Empty when the directory does not
    /// number the entry, which means there is nothing to write and the action cannot edit the source.
    /// </summary>
    public static string HeadingTextFor(ReferenceAction action) =>
        action.Reference?.Title ?? action.Title ?? "";
}
