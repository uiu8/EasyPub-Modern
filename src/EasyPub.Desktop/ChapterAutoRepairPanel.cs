using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    internal void ApplyRepairEntries(IReadOnlyList<ChapterTreeEntry> entries)
    {
        Mutate(() =>
        {
            Roots.Clear();
            foreach (var root in BuildTree(entries)) Roots.Add(root);
            _selectedNode = Flatten().FirstOrDefault();
        }, markManual: false);
        // The pass just wrote the directory it used next to this source hash, so re-read it: the rows can
        // now name the chapters the release has and this file does not.
        InvalidateBreakpoints();
        SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
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
            var outcome = await ChapterAutoRepair.RepairAsync(snapshot.SourcePath, cancellation.Token, sink,
                snapshot, reference);
            cancellation.Token.ThrowIfCancellationRequested();
            finished = true;
            progress?.Close();
            IsEnabled = true;

            if (!outcome.CatalogFound)
            {
                SetReviewResult(outcome.Verdict);
                // Nothing to confirm without a directory, so the caller's own dialog is where the
                // directory gets supplied. Opened here rather than by the caller so every button shows
                // the same sentence before the same window appears.
                CatalogAssist_Click(this, new RoutedEventArgs());
                return;
            }

            // The window states what will change and lets every change be struck out; the applying
            // half stays here, so the backup, the removal receipt and the undo hint still happen
            // exactly where they did before. Only the selection comes from the user now.
            IReadOnlyCollection<ReferenceAction>? chosen = null;
            var dialog = new RepairReviewWindow(outcome, snapshot.Entries, actions => chosen = actions, snapshot)
            { Owner = _rulesDialog ?? this };
            if (dialog.ShowDialog() != true || chosen is null)
            { SetReviewResult("未应用修复方案，当前章节树保持不变。"); return; }

            IsEnabled = false;
            // Rebuilt before anything is written: a plan that cannot be applied must fail here, where
            // the tree and the source are still untouched, not after a backup has been taken.
            var applied = ChapterAutoRepair.RebuildWithSelection(snapshot, outcome, chosen);
            if (applied.Count == 0) throw new InvalidDataException("修复方案没有章节树。");
            var backup = SourceBackupStore.CreateDefault().EnsureSnapshot(snapshot);
            var receipt = await RepairIntegrity.SaveRemovedAsync(snapshot, outcome.RemovedSourceLines);
            if (outcome.Catalog is { } catalog) ReferenceCatalogInput.SaveCatalog(snapshot.SourceSha256, catalog);
            ApplyRepairEntries(applied);
            SetReviewResult(outcome.Verdict + "。可撤销。备份：" + backup
                + (receipt is null ? "" : "；移除清单：" + receipt));
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
