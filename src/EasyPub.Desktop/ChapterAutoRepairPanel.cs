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
            var dialog=ThemedWindow("目录修复 · 核对后应用",760,580);
            var panel=new DockPanel { Margin=new Thickness(20) };
            var footer=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right };
            var apply=new Button { Content="应用修复方案", IsDefault=true };
            if (outcome.Report.Any(group => group.Label == "来源与文件对不上"))
            {
                var verified = new CheckBox { Content = "已核实参考目录确属本书此版本", Margin = new Thickness(0, 6, 12, 0) };
                apply.IsEnabled = false;
                verified.Checked += (_, _) => apply.IsEnabled = true;
                verified.Unchecked += (_, _) => apply.IsEnabled = false;
                footer.Children.Add(verified);
            }
            var close=new Button { Content="暂不应用", IsCancel=true };
            footer.Children.Add(apply); footer.Children.Add(close);
            DockPanel.SetDock(footer,Dock.Bottom); panel.Children.Add(footer);
            var details=new TextBox { IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            var lines=new List<string>
            {
                "参考来源："+outcome.CatalogSource,
                "目录页面："+outcome.Catalog?.PageTitle,
                "参考目录仅用于核对结构，不代表正文已经完整。",
                "",outcome.Verdict,
                $"本次从成品移除 {outcome.RemovedLines} 行重复内容；应用前保存备份及逐行清单。",
                "已编辑的章节范围保留；原始 TXT 不变；应用后可以撤销。"
            };
            if(outcome.Missing>0)
            {
                lines.Add("\n未定位章节（可能是缺内容、标题差异或先前已删除，不能直接认定需要重新下载）：");
                lines.AddRange(outcome.MissingTitles);
                lines.Add("\n下一步：在目录辅助修复中核对来源和标题，再对照原文；只有确认原文缺失时才补充书稿。");
            }
            if(outcome.NeedsReview>0) lines.Add("\n同名但正文不同的条目已保留。请在「目录辅助修复」中逐项核对后决定是否去重。");
            // Categorised account of the repair. Long books produce hundreds of actions, so each
            // category shows its count plus one concrete example (or the volume ranges) and the rest
            // stays folded away — enough to judge the result without reading a wall of chapter titles.
            foreach(var group in outcome.Report)
            {
                lines.Add("");
                lines.Add($"── {group.Headline} ──");
                lines.Add("   "+group.Summary);
                foreach(var item in group.Items)
                    lines.Add($"   · {(item.Line>0?$"第 {item.Line} 行 ":"")}{item.Title}：{item.Detail}");
            }
            lines.Add("\n── 实际变化（以本次应用结果为准）──");
            lines.AddRange(RepairIntegrity.DescribeChanges(snapshot.Entries, outcome.Entries ?? snapshot.Entries));
            details.Text=string.Join(Environment.NewLine,lines);
            panel.Children.Add(details); dialog.Content=panel;
            apply.Click+=(_,_)=>dialog.DialogResult=true;
            if(dialog.ShowDialog()!=true) { SetReviewResult("未应用修复方案，当前章节树保持不变。"); return; }
            IsEnabled = false;
            var backup=SourceBackupStore.CreateDefault().EnsureSnapshot(snapshot);
            var receipt=await RepairIntegrity.SaveRemovedAsync(snapshot,outcome.RemovedSourceLines);
            if (outcome.Catalog is { } catalog) ReferenceCatalogInput.SaveCatalog(snapshot.SourceSha256, catalog);
            ApplyRepairEntries(outcome.Entries ?? throw new InvalidDataException("修复方案没有章节树。"));
            SetReviewResult(outcome.Verdict+"。可撤销。备份："+backup+(receipt is null ? "" : "；移除清单："+receipt));
        }
        catch(OperationCanceledException) { SetReviewResult("目录修复已取消，当前章节树保持不变。"); }
        catch(Exception ex) { SetReviewResult("未应用修复："+ex.Message); }
        finally { finished=true; progress.Close(); IsEnabled=true; }
    }
}
