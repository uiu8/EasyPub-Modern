using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 原文换版本时，内存里的模型怎么跟着换。
///
/// 这一层要挡住两类错误，两类的后果都是静默的：
///
///   1. **行号错位**：树还指着旧行号，程序照旧显示，用户以为自己编辑的那一章还在。
///   2. **手工编排被覆盖**：迁移顺手把用户编排过的树换成"重新识别的结果"，看起来一切正常，
///      只是用户做过的事全没了。这一条实测抓到过一次，所以有一个专门的测试。
///
/// 每个测试都在临时目录里操作，样书本体不参与写入。
/// </summary>
public class SourceVersionTransitionTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-transition-tests-" + Guid.NewGuid().ToString("N"));

    public SourceVersionTransitionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>一本四章、每章标题 + 正文段落的小书，行号好数。</summary>
    private static string BookText() =>
        "第一章 起点\n" +
        "正文一。\n" +
        "第二章 中途\n" +
        "正文二。\n" +
        "第三章 转折\n" +
        "正文三。\n" +
        "第四章 终点\n" +
        "正文四。\n";

    private async Task<(string Path, SourceTransitionState State)> LoadAsync(string? text = null)
    {
        var path = Path.Combine(_workspace, "书稿.txt");
        await File.WriteAllTextAsync(path, text ?? BookText(), new UTF8Encoding(false));
        var state = await SourceTransitionState.LoadFromAsync(path);
        return (path, state);
    }

    /// <summary>把某一行的文字换掉，模拟一次"改标题"的修复。</summary>
    private static SourcePatch ReplacePatch(SourceTextDocument original, int line, string newText) =>
        new("t", "sha", [new ReplaceOriginalLine(line, original.Lines[line - 1].Text, newText)
            { OperationId = $"rep-{line}", ActionId = "a" }]);

    private static SourcePatch DeletePatch(SourceTextDocument original, params int[] lines) =>
        new("t", "sha", lines.Select(line => (SourceOperation)new DeleteOriginalLine(line,
            original.Lines[line - 1].Text) { OperationId = $"del-{line}", ActionId = "a" }).ToArray());

    private static async Task<SourceTransitionResult> MigrateAsync(string path, SourceTransitionState state,
        SourceTextDocument original, SourcePatch patch)
    {
        var rendered = SourcePatchRenderer.Render(original, patch);
        await File.WriteAllBytesAsync(path, SourceFileHasher.BytesOf(original, rendered.Text));
        return await SourceVersionTransition.AfterReplaceAsync(state, patch, rendered.Text, path);
    }

    /// <summary>把区间列表印成「开始–结束、开始–结束」，断言失败时一眼能看出形状。</summary>
    private static string Show(IReadOnlyList<ChapterSourceRange> ranges) =>
        ranges.Count == 0 ? "（无）" : string.Join("、", ranges.Select(range => $"{range.StartLine}–{range.EndLine}"));

    [Fact]
    public async Task A_state_read_from_disk_has_nothing_left_to_migrate()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var rendered = SourcePatchRenderer.Render(original, patch);
        await File.WriteAllBytesAsync(path, SourceFileHasher.BytesOf(original, rendered.Text));

        // 从磁盘读到的新状态：识别树、文本、哈希都是新版本的，所以没有一步需要执行。
        var fresh = await SourceTransitionState.LoadFromAsync(path);
        var result = await SourceVersionTransition.AfterReplaceAsync(fresh, patch, rendered.Text, path);

        Assert.True(result.Succeeded, result.Message);
        // 没有失败、没有身份要搬、"没有回退"。不去数"执行了几步"：读一遍文本和核对一次坐标表本来就要做，
        // 那两步执行是正常的，被检查的应该是"有没有东西被搬动"。
        Assert.DoesNotContain(result.Steps, step => step.Outcome == SourceTransitionStepOutcome.Failed);
        Assert.Empty(result.Rebinds);
        Assert.False(result.FellBackToRecognition);
    }

    [Fact]
    public async Task A_rewritten_heading_line_loses_its_chapter_identity()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        // 第 3 行是第二章的标题行。它被改写过，所以"L3"这个身份不能悄悄跟到新行号上去。
        var lost = Assert.Single(result.Lost);
        Assert.Equal("L3", lost.OldStableKey);
        Assert.Equal(SourceRebindStatus.LostLineReplaced, lost.Status);
        Assert.DoesNotContain(result.State.Plan!.Entries, entry => entry.StableKey == "L3");
        // 其余章节都在。
        Assert.Contains(result.State.Plan.Entries, entry => entry.StableKey == "L1");
        Assert.Contains(result.State.Plan.Entries, entry => entry.StableKey == "L5");
    }

    [Fact]
    public async Task A_chapter_whose_heading_line_was_deleted_keeps_its_identity_by_title()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = DeletePatch(original, 3);
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        var rebound = Assert.Single(result.Rebinds, rebind => rebind.OldStableKey == "L3");
        Assert.Equal(SourceRebindStatus.LostLineDeleted, rebound.Status);
        // 它还有正文，所以以一个"标题键"的身份活下来 —— 正是"待插入标题"那一类身份。
        Assert.Equal("T第二章 中途", rebound.NewStableKey);
        Assert.Contains(result.State.Plan!.Entries, entry => entry.StableKey == "T第二章 中途");
    }

    [Fact]
    public async Task A_chapter_with_no_line_and_no_body_left_is_dropped()
    {
        // 只有标题、没有正文的章节：删掉标题行之后它什么都不剩了。
        var (path, state) = await LoadAsync("第一章 起点\n第二章 空的\n第三章 终点\n正文三。\n");
        var original = state.Source;
        var empty = state.Plan!.Entries.Single(entry => entry.Title == "第二章 空的");
        Assert.Equal(2, empty.TitleLineNumber);
        Assert.Empty(empty.ContentRanges);

        var patch = DeletePatch(original, 2);
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        var dropped = Assert.Single(result.Rebinds, rebind => rebind.OldStableKey == "L2");
        Assert.Equal(SourceRebindStatus.LostNoBody, dropped.Status);
        // 标题也不在了：它既没有标题行、也没有正文，留着就是一棵树上的空壳。
        Assert.DoesNotContain(result.State.Plan!.Entries, entry => entry.Title == "第二章 空的");
    }

    [Fact]
    public async Task Surviving_lines_move_to_the_new_numbering()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = DeletePatch(original, 2);
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        // 删掉第 2 行后，第三章的标题从第 5 行挪到第 4 行。
        var moved = Assert.Single(result.Rebinds, rebind => rebind.OldStableKey == "L5");
        Assert.Equal(SourceRebindStatus.Rebound, moved.Status);
        Assert.Equal("L4", moved.NewStableKey);
        var entry = Assert.Single(result.State.Plan!.Entries, candidate => candidate.StableKey == "L4");
        Assert.Equal(4, entry.TitleLineNumber);
        Assert.Equal("第三章 转折", entry.Title);
    }

    [Fact]
    public async Task Reloading_the_recognition_tree_does_not_replace_the_users_own_tree()
    {
        // 用户在树上做过编排（这里用一个"打算插入、原文里还没有"的章节代表），迁移必须把它一起搬过去，
        // 而不是换成"重新识别的结果"。实测里这正是被踩到过的坑：树的条目数从 469 变成 468，
        // 迁移报告"没什么要搬的"，用户做过的事全没了。
        var (path, state) = await LoadAsync();
        var original = state.Source;

        var planned = new ChapterTreeEntry("planned-1", "【待插入】尾声", 1, true, null, [])
        {
            HeadingLevel = 1,
            RecognitionSource = "planned",
        };
        var withPlan = state with
        {
            Plan = state.Plan! with { Entries = state.Plan.Entries.Append(planned).ToArray() },
        };

        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var result = await MigrateAsync(path, withPlan, original, patch);

        Assert.True(result.Succeeded, result.Message);
        // 用户编排的章节还在，而且身份没变。
        Assert.Contains(result.State.Plan!.Entries, entry => entry.StableKey == "T【待插入】尾声");
        // 识别树是重新读的（468 之类），但计划不是它的副本。
        Assert.NotNull(result.State.RecognitionTree);
        Assert.NotSame(result.State.Plan.Entries, result.State.RecognitionTree.Entries);
    }

    [Fact]
    public async Task Confirmed_reviews_and_breakpoints_are_retired()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var withReviews = state with
        {
            ConfirmedReviews = new Dictionary<string, ChapterReviewGroup>
            {
                ["旧-1"] = new(new ConversionPreflightIssue(path, PreflightSeverity.Warning, "chapter_unnumbered",
                    "旧复核"), "编号不连续", ["a", "b"], [2, 3], []),
            },
            Breakpoints = [new ChapterBreakpoint(ChapterBreakpointKind.NumberGap, "a", "b", 1, [2], [], "旧断点")],
        };

        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var result = await MigrateAsync(path, withReviews, original, patch);

        Assert.True(result.Succeeded, result.Message);
        // 复核是按旧行号确认的，迁移没有诚实的办法把它改成新行号，所以它必须被作废而不是被改写。
        Assert.Null(result.State.ConfirmedReviews);
        Assert.Null(result.State.Breakpoints);
        // 这两样东西确实被那一步处理过：要么那一步作废了它们，要么前一步（重建计划）已经作废。
        // 这里断言的是结果，不是哪一步做的 —— 断言"哪一步做的"会把实现细节钉死。
        var handled = result.Steps.Any(step => step.Step.Contains("复核")
            && step.Outcome is SourceTransitionStepOutcome.Completed or SourceTransitionStepOutcome.AlreadySatisfied);
        Assert.True(handled);
    }

    [Fact]
    public async Task Migrating_twice_does_nothing_the_second_time()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var first = await MigrateAsync(path, state, original, patch);
        Assert.True(first.Succeeded, first.Message);

        var rendered = SourcePatchRenderer.Render(original, patch);
        var second = await SourceVersionTransition.AfterReplaceAsync(first.State, patch, rendered.Text, path);

        Assert.True(second.Succeeded, second.Message);
        // 第二次没有任何身份需要重绑，也没有步骤失败 —— 这就是"幂等"在这里的含义。
        Assert.Empty(second.Rebinds);
        Assert.DoesNotContain(second.Steps, step => step.Outcome == SourceTransitionStepOutcome.Failed);
        Assert.False(second.FellBackToRecognition);
        Assert.Equal(first.State.Plan!.Entries.Select(entry => entry.StableKey),
            second.State.Plan!.Entries.Select(entry => entry.StableKey));
    }

    [Fact]
    public async Task A_patch_the_new_text_does_not_satisfy_is_refused()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        // 补丁要求改三处标题。
        var patch = new SourcePatch("t", "sha",
        [
            new ReplaceOriginalLine(1, original.Lines[0].Text, "第一章 甲") { OperationId = "rep-1", ActionId = "a" },
            new ReplaceOriginalLine(3, original.Lines[2].Text, "第二章 乙") { OperationId = "rep-3", ActionId = "a" },
            new ReplaceOriginalLine(5, original.Lines[4].Text, "第三章 丙") { OperationId = "rep-5", ActionId = "a" },
        ]);

        // 但落到磁盘上的文本只兑现了其中两处 —— 第 3 行没改。
        var halfway = patch.Operations
            .Where(operation => operation is ReplaceOriginalLine { Line: not 3 })
            .ToArray();
        var expected = SourcePatchRenderer.Render(original,
            new SourcePatch("t", "sha", halfway)).Text;
        await File.WriteAllBytesAsync(path, SourceFileHasher.BytesOf(original, expected));

        var result = await SourceVersionTransition.AfterReplaceAsync(state, patch, expected, path);

        Assert.False(result.Succeeded);
        Assert.Contains("第 3 行", result.Message);
        Assert.DoesNotContain(result.Steps, step => step.Outcome == SourceTransitionStepOutcome.Completed);
    }

    [Fact]
    public async Task An_empty_patch_beside_a_rewritten_text_is_refused()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var real = ReplacePatch(original, 3, "第二章 改过的标题");
        var rendered = SourcePatchRenderer.Render(original, real);

        // 文本变了，却没有一个操作认领这次变化。
        var result = await SourceVersionTransition.AfterReplaceAsync(
            state, SourcePatch.Empty(state.SourceSha256), rendered.Text, path);

        Assert.False(result.Succeeded);
        Assert.Contains("第 3 行", result.Message);
    }

    [Fact]
    public async Task An_untracked_edit_is_refused()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = DeletePatch(original, 8);
        var rendered = SourcePatchRenderer.Render(original, patch);

        // 补丁之外还偷偷改了一行。
        var tampered = rendered.Text.Replace("第一章 起点", "第一章 偷偷改过", StringComparison.Ordinal);
        var result = await SourceVersionTransition.AfterReplaceAsync(state, patch, tampered, path);

        Assert.False(result.Succeeded);
        Assert.Contains("偷偷改过", result.Message);
    }

    [Fact]
    public async Task The_saved_directory_follows_the_book_to_its_new_hash()
    {
        // 目录按源文件的哈希存放，所以换一版原文之后按新哈希什么都找不到 —— 用户看到的是"程序忘了我给的目录"。
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var rendered = SourcePatchRenderer.Render(original, patch);
        var newSha = SourceFileHasher.HashOfRendered(original, rendered.Text);

        var oldHash = state.RecognitionTree!.SourceSha256;
        var oldPath = ReferenceCatalogInput.SettingsPath(oldHash);
        var newPath = ReferenceCatalogInput.SettingsPath(newSha);
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        if (File.Exists(newPath)) File.Delete(newPath);
        ReferenceCatalogInput.Save(oldHash, new CatalogPreferences("查询词", "https://example.invalid",
            "第一章 起点\n第二章 中途\n", 3));

        try
        {
            await File.WriteAllBytesAsync(path, SourceFileHasher.BytesOf(original, rendered.Text));
            var result = await SourceVersionTransition.AfterReplaceAsync(
                state, patch, rendered.Text, path, oldCatalogPath: oldPath);

            Assert.True(result.Succeeded, result.Message);
            var copied = ReferenceCatalogInput.Load(newSha);
            Assert.NotNull(copied);
            Assert.Equal("查询词", copied.Query);
            Assert.Contains("第二章 中途", copied.Text);
        }
        finally
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (File.Exists(newPath)) File.Delete(newPath);
        }
    }

    [Fact]
    public async Task The_tree_provenance_follows_the_book_to_its_new_hash()
    {
        // provenance 是**逐字段重建 plan** 时最容易漏掉的东西：SourceVersionTransition.BuildPlan
        // 手工复制每一个字段，漏掉一个就等于它在原文迁移之后**静默消失**。
        //
        // 而它一旦消失，一棵目录树在迁移之后就会被当成"本地识别" —— 状态条显示错的来源，
        // 用户没有任何办法发现。这条测试钉住那个窄例外：事务算法不改，但迁移必须把新状态带过去。
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = ReplacePatch(original, 3, "第二章 改过的标题");
        var rendered = SourcePatchRenderer.Render(original, patch);

        var marked = state with
        {
            // Plan 才是迁移真正搬运的那个对象；RecognitionTree 只是识别结果，不带 provenance。
            Plan = state.RecognitionTree!.CreatePlan(state.RecognitionTree.Entries) with
            {
                Provenance = PersistedTreeProvenance.FromCatalog("https://example.invalid/目录"),
            },
        };
        var oldCatalogPath = ReferenceCatalogInput.SettingsPath(state.RecognitionTree!.SourceSha256);

        await File.WriteAllBytesAsync(path, SourceFileHasher.BytesOf(original, rendered.Text));
        var result = await SourceVersionTransition.AfterReplaceAsync(
            marked, patch, rendered.Text, path, oldCatalogPath: oldCatalogPath);

        Assert.True(result.Succeeded, result.Message);
        var moved = result.State.Plan?.Provenance;
        Assert.NotNull(moved);
        Assert.Equal(TreeBaseOrigin.Reference, moved!.BaseOrigin);
        Assert.Equal("https://example.invalid/目录", moved.AppliedCatalogFingerprint);
    }

    [Fact]
    public async Task Every_migrated_body_line_comes_from_the_line_it_claims()
    {
        // 这一条是整层最核心的不变量：迁移后每个条目的每一行，都必须来自该条目自己原来的正文。
        //
        // 它同时钉住两件事：行号没有错位，以及没有把别人的正文吞进来。实测里"吞"是真实发生过的形状 ——
        // 相邻区间一旦合并，前面那一章就会连标题带正文吃掉后面那一章。
        var (path, state) = await LoadAsync(
            "第一章 起点\n" +
            "正文一。\n" +
            "第二章 长章节\n" +
            "正文甲。\n" +
            "正文乙。\n" +
            "正文丙。\n" +
            "正文丁。\n" +
            "正文戊。\n" +
            "正文己。\n" +
            "第三章 终点\n" +
            "正文三。\n");
        var original = state.Source;
        var patch = DeletePatch(original, 5, 6);
        var rendered = SourcePatchRenderer.Render(original, patch);
        var map = SourceCoordinateMap.Build(original, patch, rendered.Text);
        Assert.True(map.Verify(original, patch, rendered.Text).IsSound);

        var result = await MigrateAsync(path, state, original, patch);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.State.Plan is not null);
        ChapterTreeDocument.ValidatePlan(result.State.Plan!, result.State.RecognitionTree!.LineCount);

        var before = state.Plan!.Entries.ToDictionary(entry => entry.StableKey);
        foreach (var entry in result.State.Plan!.Entries)
        {
            // 找到它在迁移前的样子：身份要么没变，要么是从旧键搬过来的。
            var rebind = result.Rebinds.FirstOrDefault(candidate => candidate.NewStableKey == entry.StableKey);
            var oldKey = rebind?.OldStableKey ?? entry.StableKey;
            if (!before.TryGetValue(oldKey, out var previous)) continue;

            var allowed = new HashSet<int>();
            foreach (var range in previous.ContentRanges)
                for (var line = range.StartLine; line <= range.EndLine; line++)
                    if (map.TryMap(line) is { } mapped) allowed.Add(mapped);
            foreach (var range in entry.ContentRanges)
                for (var line = range.StartLine; line <= range.EndLine; line++)
                    Assert.Contains(line, allowed);
        }

        // 第二章原本的正文 4–9，删掉中间两行之后仍然是连续的 4–7。
        var chapter = result.State.Plan.Entries.Single(entry => entry.Title == "第二章 长章节");
        Assert.Equal("4–7", string.Join("、",
            chapter.ContentRanges.Select(range => $"{range.StartLine}–{range.EndLine}")));
    }

    [Fact]
    public async Task Deleting_a_whole_chapter_does_not_hand_its_lines_to_its_neighbour()
    {
        // 中间那一章连标题带正文一起被删掉之后，它前后的两章在新行号下会贴在一起。
        // 贴在一起不等于同一章：合并会把后一章整个吞掉。
        var (path, state) = await LoadAsync(
            "第一章 起点\n" +
            "正文一。\n" +
            "第二章 被牺牲的\n" +
            "正文二。\n" +
            "第三章 终点\n" +
            "正文三。\n");
        var original = state.Source;
        var patch = DeletePatch(original, 3, 4);
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        // 第二章连标题带正文一起没了，所以它的身份消失。
        Assert.Contains(result.Rebinds, rebind => rebind.OldStableKey == "L3" && !rebind.Survived);

        var first = result.State.Plan!.Entries.Single(entry => entry.Title == "第一章 起点");
        var third = result.State.Plan.Entries.Single(entry => entry.Title == "第三章 终点");
        // 第一章只认自己那一行正文；第三章的标题和正文都还在，而且各有其位。
        Assert.Equal("2–2", Show(first.ContentRanges));
        Assert.Equal(3, third.TitleLineNumber);
        Assert.Equal("4–4", Show(third.ContentRanges));
    }

    [Fact]
    public async Task Every_rebound_entry_in_a_run_stays_inside_the_new_text()
    {
        var (path, state) = await LoadAsync();
        var original = state.Source;
        var patch = DeletePatch(original, 2, 6);
        var result = await MigrateAsync(path, state, original, patch);

        Assert.True(result.Succeeded, result.Message);
        foreach (var entry in result.State.Plan!.Entries)
            foreach (var range in entry.ContentRanges)
            {
                Assert.InRange(range.StartLine, 1, result.State.Source.Lines.Count);
                Assert.InRange(range.EndLine, range.StartLine, result.State.Source.Lines.Count);
            }
        // 迁移后的树必须能通过正式校验，否则它就不该被交给后面的流程。
        ChapterTreeDocument.ValidatePlan(result.State.Plan, result.State.RecognitionTree!.LineCount);
    }

    // ── 另一条换版本的路径：文件是本程序之外改的，手里没有任何补丁 ────────────────────────────

    /// <summary>直接在磁盘上改文本，然后问迁移该怎么做：没有补丁，只有前后两个版本。</summary>
    private static async Task<SourceTransitionResult> EditOutsideAsync(string path, SourceTransitionState state,
        Func<string, string> edit)
    {
        await File.WriteAllTextAsync(path, edit(await File.ReadAllTextAsync(path)), new UTF8Encoding(false));
        return await SourceVersionTransition.AfterExternalEditAsync(state, path);
    }

    [Fact]
    public async Task An_edit_made_outside_the_program_keeps_the_chapters_it_did_not_touch()
    {
        // 有人在记事本里改了一句正文。标题行一个没动，所以每一章的身份都该跟过来 ——
        // 而不是"原文变了，整棵树重新识别"。
        var (path, state) = await LoadAsync();
        var result = await EditOutsideAsync(path, state, text => text.Replace("正文三。", "正文三，改过一句。"));

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.FellBackToRecognition);
        Assert.Equal(state.Plan!.Entries.Count, result.Rebinds.Count);
        Assert.All(result.Rebinds, rebind => Assert.True(rebind.Survived, rebind.Title));
        Assert.Equal(state.Plan.Entries.Count, result.State.Plan!.Entries.Count);
        Assert.Contains("正文三，改过一句。", result.State.Source.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task An_edit_outside_the_program_that_deletes_a_heading_reports_that_chapter_as_lost()
    {
        var (path, state) = await LoadAsync();
        // 连标题带正文删掉第二章：它前后的两章在新行号下会贴在一起，但那是两章，不是一章。
        var result = await EditOutsideAsync(path, state, text => text.Replace("第二章 中途\n正文二。\n", ""));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(result.Rebinds, rebind => rebind.Title == "第二章 中途" && !rebind.Survived);
        // 没被碰过的那几章不受影响：一次外部编辑不该连累它们。
        Assert.Equal(state.Plan!.Entries.Count - 1, result.Rebinds.Count(rebind => rebind.Survived));
    }

    [Fact]
    public async Task An_unchanged_file_is_not_reported_as_a_failed_migration()
    {
        // 这个检查在工作台每次激活时都会跑，而多数激活之前文件根本没变。
        // 在那里报一次失败，等于教用户忽略真正要紧的那条消息。
        var (path, state) = await LoadAsync();
        var result = await SourceVersionTransition.AfterExternalEditAsync(state, path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(result.Rebinds);
        Assert.Contains("未变", result.Message);
        Assert.Contains(result.Steps, step => step.Outcome == SourceTransitionStepOutcome.AlreadySatisfied);
    }
}
