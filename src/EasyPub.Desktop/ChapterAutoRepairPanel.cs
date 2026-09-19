using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    /// <summary>
    /// Adopts the tree a repair produced, and — when the repair also wrote the TXT — the new version of the
    /// book itself.
    ///
    /// <para>Taking the whole document rather than only its entries is what keeps the window honest about
    /// which version it is showing. While this only replaced the rows, <c>_document</c> still described the
    /// pre-repair file, so a repair that had just written the TXT left the window claiming "原文已变化 · 刷新"
    /// about the program's own edit.</para>
    /// </summary>
    internal void ApplyRepairEntries(ChapterTreeDocument document, string? catalogFingerprint = null)
    {
        // A snapshot written in the old numbering cannot be replayed onto the new one, so the history goes
        // with the version it belongs to.
        var changedSource = !string.Equals(document.SourceSha256, _document.SourceSha256, StringComparison.OrdinalIgnoreCase);
        _document = document;
        // 这棵树刚刚被**目录方案**改过 —— 来源标记必须跟着走。它是唯一能回答"当前树是怎么来的"的
        // 事实来源：RecognitionSource 装不下它（手工编辑会覆盖 reference 标记），而
        // ChapterTreePlan.ReferenceCatalog 只证明"保存过一份目录"。
        //
        // 标记放在这里而不是调用点，是因为两个落地模式都经过这个方法 —— 漏掉任一处，
        // 那棵树在版本迁移之后就会被当成"本地识别"。
        _provenance = PersistedTreeProvenance.FromCatalog(catalogFingerprint);
        _sourceChanged = false;
        _baselineSource = TryLoadBaseline(document.SourcePath, _encodingMode);
        if (changedSource) { _undo.Clear(); _redo.Clear(); _confirmedGroups.Clear(); }
        Mutate(() =>
        {
            Roots.Clear();
            foreach (var root in BuildTree(document.Entries)) Roots.Add(root);
            _selectedNode = Flatten().FirstOrDefault();
        }, markManual: false);
        // The pass just wrote the directory it used next to this source hash, so re-read it: the rows can
        // now name the chapters the release has and this file does not.
        InvalidateBreakpoints();
        SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons(); UpdateSaveState(); UpdateUndoRedoButtons();
    }

    private async void AutoRepair_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged || !IsEnabled) return;
        await RunRepairReviewAsync();
    }

    /// <summary>
    /// A progress sink that is safe to call from any thread.
    ///
    /// <see cref="Progress{T}"/> looks harmless but captures the <c>SynchronizationContext</c> it was
    /// constructed on, and **falls back to the thread pool when there is none** — so its callback runs
    /// off the UI thread and the first thing it touches throws. From a button click that never shows
    /// because WPF installs a context first; drive this window from a harness thread without one, which
    /// is exactly what the sample-book test does, and the process dies with an unhandled
    /// cross-thread exception. Marshalling explicitly removes the dependency on who called us.
    /// </summary>
    private static IProgress<string> ProgressOn(Dispatcher dispatcher, Action<string> apply) =>
        new DispatcherProgress(dispatcher, apply);

    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<string> apply) : IProgress<string>
    {
        public void Report(string value)
        {
            if (dispatcher.CheckAccess()) apply(value);
            else dispatcher.BeginInvoke(() => apply(value));
        }
    }

    /// <summary>
    /// The document the workbench should carry on with after the TXT was rewritten.
    ///
    /// <para>The plan comes from the migration — it is the tree the user arranged, moved onto the new line
    /// numbers — and the source lines come from a fresh parse of the file the transaction just wrote. Both
    /// halves are needed: the plan has no line text, and a re-recognition would not have the user's
    /// arrangements.</para>
    /// </summary>
    private static ChapterTreeDocument ReloadAfterEdit(RepairApplicationResult result, ChapterTreeDocument before)
    {
        var plan = result.State?.Plan;
        if (plan is null) throw new InvalidDataException("原文已替换，但没有得到迁移后的章节树。");
        return ChapterTreeDocument.Load(before.SourcePath, File.ReadAllBytes(before.SourcePath),
            chapterPattern: null, hierarchy: before.RecognitionOptions, existingPlan: plan);
    }

    /// <summary>
    /// The one repair entry every button goes through: take the current tree, build the proposal, show it
    /// in <see cref="RepairReviewWindow"/>, and only then hand the selection to the applier.
    ///
    /// This method exists because it used to not: the 「一键目录修复」button called it, while
    /// 「手动核对目录…」opened a second dialog with its own grid, its own checkbox set and its own
    /// confirmation window, ending in the same <c>ReferencePlanner.Apply</c> call by a different route.
    /// Two windows that claimed to preview the same repair drifted apart — the manual one could not show
    /// the report categories, and this one could not run from a directory the user had just pasted.
    /// Routing both here is what makes "the button does what the code does" true rather than hopeful.
    ///
    /// <paramref name="reference"/> is the directory to use. When it is null the saved directory is read
    /// (and the network is asked only if there is none), which is the one-click behaviour. When it is not
    /// null it is used as given — that is how a just-pasted directory reaches this pass without a disk
    /// round-trip, and how the confirmation window ends up showing the caller's directory rather than a
    /// stale saved one.
    /// </summary>
    private async Task RunRepairReviewAsync(ReferenceCatalog? reference = null,
        Action<string>? report = null)
    {
        if (_sourceChanged || !IsEnabled) return;
        var snapshot = _document.WithEntries(Flatten().Select(node => node.ToEntry()).ToArray());
        // The tree the reader arranged, as a plan that can be saved beside the bytes it belongs to.
        //
        // Every snapshot this window took used to be bytes-only, because nothing ever handed the second
        // argument over — so 「恢复到当时的完整书稿状态」 was offered by the restore layer and could never be
        // chosen in the interface. Passing it here is what makes that option real.
        var snapshotPlan = snapshot.CreatePlan(snapshot.Entries);
        using var cancellation = new CancellationTokenSource();

        // The standalone button owns its progress window; a caller that already has a dialog passes a
        // report callback instead, so the two never stack.
        Window? progress = null;
        var stage = default(TextBlock);
        if (report is null)
        {
            progress = ThemedWindow("目录修复 · 正在准备方案", 510, 240);
            var stack = new StackPanel { Margin = new Thickness(22) };
            stage = new TextBlock { Text = "正在读取当前章节树…", TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            stack.Children.Add(stage);
            stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 12) });
            stack.Children.Add(new TextBlock { Text = "先预览，再应用。已删除内容不会恢复；原始 TXT 保持不变。", TextWrapping = TextWrapping.Wrap });
            var stop = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            stop.Click += (_, _) => { cancellation.Cancel(); stage.Text = "正在取消…"; stop.IsEnabled = false; };
            stack.Children.Add(stop);
            progress.Content = stack;
            progress.Closing += (_, _) => cancellation.Cancel();
            progress.Show();
        }

        IsEnabled = false;
        var finished = false;
        try
        {
            var sink = report is null
                ? ProgressOn(Dispatcher, text => { if (stage is not null) stage.Text = text; })
                : ProgressOn(Dispatcher, report);
            // 依据**只读本书已保存的目录，不联网**。这是主按钮能"按依据变名"的前提：
            // 按钮上写的依据，必须在点下去之前就是真的。
            //
            // 联网取目录是「选择参考目录…」那个按钮的事，而且它会**换掉依据** ——
            // 一个会改变按钮含义的动作，不能藏在按钮自己里面。以前正是这么藏的：
            // 同一个「预览目录修复」会依次读保存的目录、联网抓、抓不到再退回自修复，
            // 于是它有四种结果，而用户点之前一种都看不出来（§1.1）。
            var acquisition = reference is { } given
                ? CatalogAcquisition.Given(given)
                : ChapterAutoRepair.ReadSavedCatalog(snapshot);
            // 依据是这一整条路上最要紧的岔路口：选错它，后面每个数字都是错的。
            InteractionLog.Decision("选择依据", new
            {
                来源 = reference is null ? "磁盘上保存的目录" : "调用方给定的目录",
                有目录 = acquisition.Catalog is not null,
                章数 = acquisition.Catalog?.Titles.Count ?? 0,
                出处 = acquisition.Catalog?.Source,
            });
            // 显式声明成基类型：两个分支是不同的 sealed record，三元表达式自己推不出共同类型。
            RepairRequest request = acquisition.Catalog is { } found
                ? new ReferenceRepairRequest(found)
                : new CurrentTreeHeuristicRequest();
            var outcome = await ChapterAutoRepair.RepairAsync(snapshot.SourcePath, request, cancellation.Token,
                sink, snapshot, acquisition);
            cancellation.Token.ThrowIfCancellationRequested();
            finished = true;
            progress?.Close();
            IsEnabled = true;
            InteractionLog.Outcome("生成方案", new
            {
                有目录 = outcome.CatalogFound,
                动作数 = outcome.Plan?.Actions.Count ?? 0,
                重建卷 = outcome.RebuiltVolumes,
                重建章 = outcome.RebuiltChapters,
                未定位 = outcome.Missing,
            });

            // **无方案时不再自动弹目录对话框**（设计文档 §6 情况 C）。
            //
            // 以前这里是"没有目录 + 没有方案 → 直接打开核对面板"，等于把"目录"当成唯一出路。
            // 但方案为空有两个完全不同的原因，要说不同的话：
            //   · 已有目录 → 按目录核对后，树与原文确实没有需要改动的地方；
            //   · 没有目录 → 只查了"树自身能看出的问题"，缺章与漏识别标题**根本没被检查**。
            // 后者尤其不能含糊：不说清的话，用户会以为"没问题"，而实际上是"问题没查"。
            if ((outcome.Plan?.Actions.Count ?? 0) == 0)
            {
                SetReviewResult(outcome.CatalogFound
                    ? "本次没有可应用的方案：按这份参考目录核对后，当前章节树与原文没有需要改动的地方。"
                    : "本次没有可应用的方案。本次依据是**当前章节树**，只覆盖树自身能看出的问题"
                      + "（例如重复标题）；缺章与漏识别标题需要参考目录才能判断 —— "
                      + "需要的话点「选择参考目录…」。");
                return;
            }

            // The window states what will change and lets every change be struck out; the applying half
            // stays here. What changed with the landing mode is that "applying" can now mean writing the
            // TXT, and that decision is made in the window and carried out by the same applier the tests
            // drive — there is no second path that only the button can reach.
            IReadOnlyCollection<ReferenceAction>? chosen = null;
            var store = SourceBackupStore.CreateDefault();
            var volumeLevels = outcome.Catalog?.VolumeTitles.Count > 0;
            // No backup callback: the applier takes the pre-edit snapshot itself, through the backup layer,
            // and hands it both the text and the tree. That is what makes 「恢复原文与当时的章节树」 possible
            // for this version later — every snapshot used to come out bytes-only.
            var applier = new ChapterRepairApplier(store.DirectoryPath);
            var dialog = new RepairReviewWindow(outcome, snapshot.Entries, actions => chosen = actions, snapshot,
                // Read from settings rather than fixed here: which mode the window opens on is a preference
                // about how much the program may do on its own, and the window still shows both options and
                // still says what the chosen one will change.
                defaultMode: AppSettingsStore.CreateDefault().Load().DefaultRepairLandingMode,
                previewMode: (mode, actions) => applier.Preview(outcome, snapshot, mode, actions, volumeLevels),
                decisionsFor: (mode, actions) => ChapterRepairApplier.DecisionsFor(
                    ChapterRepairApplier.ProposalFor(outcome, snapshot)!, mode, actions));
            // Same reason as ThemedWindow: this runs after an await, where the workbench may have been closed
            // and an exception would take the process down rather than fail the repair.
            try { dialog.Owner = _rulesDialog ?? this; }
            catch (InvalidOperationException) { }
            if (dialog.ShowDialog() != true || chosen is null)
            { SetReviewResult("未应用修复方案，当前章节树保持不变。"); return; }

            IsEnabled = false;
            // Rebuilt before anything is written: a plan that cannot be applied must fail here, where
            // the tree and the source are still untouched, not after a backup has been taken.
            var decisions = dialog.SelectedDecisions();
            var applied = ChapterAutoRepair.RebuildWithSelection(snapshot, outcome, chosen);
            if (applied.Count == 0) throw new InvalidDataException("修复方案没有章节树。");

            if (dialog.LandingMode == RepairLandingMode.TreeOnly)
            {
                // The mode that has always existed: change the tree, leave the file alone. No transaction is
                // created for it, and by the design's invariant none ever should be.
                _landingModeCache = RepairLandingMode.TreeOnly;
                var backup = store.EnsureSnapshot(snapshot, snapshotPlan);
                var receipt = await RepairIntegrity.SaveRemovedAsync(snapshot, outcome.RemovedSourceLines);
                if (outcome.Catalog is { } catalog) ReferenceCatalogInput.SaveCatalog(snapshot.SourceSha256, catalog);
                ApplyRepairEntries(_document.WithEntries(applied), outcome.Catalog?.Source);
                SetReviewResult(outcome.Verdict + "。可撤销。备份：" + backup
                    + (receipt is null ? "" : "；移除清单：" + receipt));
                return;
            }

            // Writing the book: the applier compiles the decisions, takes the backup, renders beside the
            // source, freezes the manifest, replaces atomically and then moves the tree onto the new text.
            _landingModeCache = RepairLandingMode.EditSource;
            var result = await applier.ApplyAsync(outcome, snapshot, RepairLandingMode.EditSource, chosen,
                buildVolumeLevels: volumeLevels);
            if (!result.Changed)
            {
                SetReviewResult("未改动原文：" + result.Message);
                return;
            }
            var movedDocument = ReloadAfterEdit(result, snapshot);
            if (outcome.Catalog is { } usedCatalog)
                ReferenceCatalogInput.SaveCatalog(movedDocument.SourceSha256, usedCatalog);
            ApplyRepairEntries(movedDocument, outcome.Catalog?.Source);
            SetReviewResult(outcome.Verdict + "。" + result.Message);
        }
        catch (OperationCanceledException) { SetReviewResult("目录修复已取消，当前章节树保持不变。"); }
        catch (Exception ex) { SetReviewResult("未应用修复：" + ex.Message); }
        finally
        {
            if (!finished) progress?.Close();
            IsEnabled = true;
        }
    }
}
