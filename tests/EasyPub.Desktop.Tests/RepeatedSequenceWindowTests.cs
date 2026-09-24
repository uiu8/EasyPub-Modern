using System.IO;
using System.Windows;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class RepeatedSequenceWindowTests
{
    [Fact]
    public void Sequence_pairs_keep_order_revalidate_and_select_without_mutation()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
            var app = new App(); app.InitializeComponent();
            var path = Path.Combine(Path.GetTempPath(), "sequence-picker-" + Guid.NewGuid().ToString("N") + ".txt");
            var section = string.Join("\n", Enumerable.Range(25, 4).Select(i =>
                $"第{i}章 归途{i}\n第{i}天，旅人沿着河流继续前进，终于发现远方的城堡。"));
            var text = section + "\n第29章 间隔\n另外的故事。\n" + section;
            File.WriteAllText(path, text);
            var doc = ChapterTreeDocument.Load(path, File.ReadAllBytes(path));
            var sequence = Assert.Single(ChapterRepeatedSequences.Find(doc, doc.Entries));
            var pairs = RepeatedSequenceWindow.FindPairs(doc, doc.Entries, sequence);
            Assert.Equal(4, pairs.Count);
            Assert.All(pairs, p => Assert.Equal(2, p.NodeIds.Count));
            var removed = doc.Entries.Where(e => e.Id != pairs[0].NodeIds[0]).ToArray();
            Assert.Equal(3, RepeatedSequenceWindow.FindPairs(doc, removed, sequence).Count);
            var changed = doc.Entries.Select(e => e.Id == pairs[0].NodeIds[0] ? e with { ContentRanges = [] } : e).ToArray();
            Assert.Equal(3, RepeatedSequenceWindow.FindPairs(doc, changed, sequence).Count);
            var window = new RepeatedSequenceWindow(pairs) { Style = null, Left = -4000, Top = -4000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            Exception? inner = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    window.PairList.SelectedIndex = 2;
                    WorkbenchHarness.Save(window, "重复区段逐对核对");
                    window.CompareButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                catch (Exception e) { inner = e; window.Close(); }
            };
            timer.Start();
            try { Assert.True(window.ShowDialog()); }
            finally { timer.Stop(); window.Close(); }
            Assert.Null(inner);
            Assert.Same(pairs[2], window.SelectedPair);
            Assert.Equal(text, File.ReadAllText(path));
            var empty = new RepeatedSequenceWindow([]) { Style = null };
            Assert.False(empty.CompareButton.IsEnabled);
            empty.Close();
            var previousCatalog = WorkbenchHarness.IsolateCatalogStore();
            ChapterEditorWindow? editor = null;
            try
            {
                editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(doc) { Style = null });
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var groups = (ChapterReviewGroup[])typeof(ChapterEditorWindow).GetField("_reviewGroups", flags)!.GetValue(editor)!;
                var shown = Assert.Single(groups, g => g.Issue.Code == "chapter_repeated_sequence");
                Assert.DoesNotContain(groups, g => g.Issue.Code == "chapter_content_duplicate");
                editor.LocateDuplicateChapter(shown.Issue.LineNumber!.Value);
                var choices = (System.Windows.Controls.ComboBox)editor.FindName("ChapterSuggestionsCombo");
                choices.SelectedItem = shown.Issue;
                typeof(ChapterEditorWindow).GetMethod("UpdateReviewCard", flags)!.Invoke(editor, null);
                Assert.Equal(Visibility.Visible, ((System.Windows.Controls.Button)editor.FindName("ReviewSequencePairsButton")).Visibility);
                WorkbenchHarness.Save(editor, "重复区段折叠工作台");
                typeof(ChapterEditorWindow).GetField("_searchQuery", flags)!.SetValue(editor, "归途27");
                var filtered = (ConversionPreflightIssue[])typeof(ChapterEditorWindow).GetMethod("FilteredReviewIssues", flags)!.Invoke(editor, null)!;
                Assert.Contains(shown.Issue, filtered);
                // Refresh using a tree where the first two repeated copies were removed.
                // Two surviving pairs no longer form a >=3-chapter sequence.
                var remaining = doc.Entries.Where(e => e.Id != pairs[0].NodeIds[1] && e.Id != pairs[1].NodeIds[1]).ToArray();
                typeof(ChapterEditorWindow).GetMethod("RefreshSuggestions", flags)!.Invoke(editor, [remaining]);
                var refreshed = (ChapterReviewGroup[])typeof(ChapterEditorWindow).GetField("_reviewGroups", flags)!.GetValue(editor)!;
                Assert.DoesNotContain(refreshed, g => g.Issue.Code == "chapter_repeated_sequence");
                Assert.Equal(2, refreshed.Count(g => g.Issue.Code == "chapter_content_duplicate"));
                Assert.Equal(text, File.ReadAllText(path));
            }
            finally { editor?.DiscardChangesAndClose(); WorkbenchHarness.RestoreCatalogStore(previousCatalog); }
            }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(error);
    }
}
