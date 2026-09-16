using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        var snapshot = _document.WithEntries(Flatten().Select(node => node.ToEntry()).ToArray());
        using var cancellation = new CancellationTokenSource();
        var progress = ThemedWindow("目录修复 · 正在准备方案", 510, 240);
        var stack = new StackPanel { Margin = new Thickness(22) };
        var stage = new TextBlock { Text = "正在读取当前章节树…", TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        stack.Children.Add(stage);
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0,16,0,12) });
        stack.Children.Add(new TextBlock { Text = "先预览，再应用。已删除内容不会恢复；原始 TXT 保持不变。", TextWrapping = TextWrapping.Wrap });
        var stop = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        stop.Click += (_,_) => { cancellation.Cancel(); stage.Text = "正在取消…"; stop.IsEnabled = false; };
        stack.Children.Add(stop);
        progress.Content = stack;
        var finished = false;
        progress.Closing += (_,_) => { if (!finished) cancellation.Cancel(); };
        IsEnabled = false;
        progress.Show();
        try
        {
            var reporter = new Progress<string>(text => { if (!cancellation.IsCancellationRequested) stage.Text = text; });
            var outcome = await ChapterAutoRepair.RepairAsync(snapshot.SourcePath,cancellation.Token,reporter,snapshot,SavedReference());
            cancellation.Token.ThrowIfCancellationRequested();
            finished = true; progress.Close(); IsEnabled = true;
            if (!outcome.CatalogFound)
            {
                SetReviewResult(outcome.Verdict);
                CatalogAssist_Click(this,new RoutedEventArgs());
                return;
            }
            // The window states what will change and lets every change be struck out; the applying
            // half is kept here unchanged, so the backup, the removal receipt and the undo hint still
            // happen exactly where they did before. Only the selection comes from the user now.
            IReadOnlyCollection<ReferenceAction>? chosen = null;
            var dialog = new RepairReviewWindow(outcome, snapshot.Entries, actions => chosen = actions, snapshot) { Owner = _rulesDialog ?? this };
            if (dialog.ShowDialog()!=true || chosen is null)
            { SetReviewResult("未应用修复方案，当前章节树保持不变。"); return; }
            IsEnabled = false;
            // Rebuilt before anything is written: a plan that cannot be applied must fail here, where
            // the tree and the source are still untouched, not after a backup has been taken.
            var applied = ChapterAutoRepair.RebuildWithSelection(snapshot, outcome, chosen);
            if (applied.Count == 0) throw new InvalidDataException("修复方案没有章节树。");
            var backup=SourceBackupStore.CreateDefault().EnsureSnapshot(snapshot);
            var receipt=await RepairIntegrity.SaveRemovedAsync(snapshot,outcome.RemovedSourceLines);
            if (outcome.Catalog is { } catalog) ReferenceCatalogInput.SaveCatalog(snapshot.SourceSha256, catalog);
            ApplyRepairEntries(applied);
            SetReviewResult(outcome.Verdict+"。可撤销。备份："+backup+(receipt is null ? "" : "；移除清单："+receipt));
        }
        catch(OperationCanceledException) { SetReviewResult("目录修复已取消，当前章节树保持不变。"); }
        catch(Exception ex) { SetReviewResult("未应用修复："+ex.Message); }
        finally { finished=true; progress.Close(); IsEnabled=true; }
    }
}
