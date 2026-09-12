using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class ChapterEditorWindowTests
{
    [Fact]
    public async Task Suggestions_navigate_refresh_after_edits_and_return_on_undo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chapter-suggestions-{Guid.NewGuid():N}.txt");
        var source = "第一章 开始\n正文\n第三章 继续\n正文\n第五章 结束\n正文";
        await File.WriteAllTextAsync(path, source);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                ChapterEditorWindow? window = null;
                try
                {
                    window = new ChapterEditorWindow(document) { ShowInTaskbar = false, Opacity = 0 };
                    window.ConfirmAction = _ => true;
                    window.Show();
                    window.UpdateLayout();
                    var suggestions = Assert.IsType<ComboBox>(window.FindName("ChapterSuggestionsCombo"));
                    Assert.NotNull(suggestions.ItemTemplate);
                    var next = Assert.IsType<Button>(window.FindName("NextSuggestionButton"));
                    Assert.Equal(2, suggestions.Items.Count);
                    window.NavigateToSourceLine(3);
                    Assert.Equal(0, suggestions.SelectedIndex);
                    var label = Assert.IsType<TextBlock>(suggestions.ItemTemplate.LoadContent());
                    label.DataContext = suggestions.SelectedItem;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    label.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                    Assert.Equal(Assert.IsType<ConversionPreflightIssue>(suggestions.SelectedItem).Message, label.Text);
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(1, suggestions.SelectedIndex);
                    Assert.Equal(5, Assert.IsType<ChapterTreeSourceLine>(Assert.IsType<ListBox>(window.FindName("SourceLinesList")).SelectedItem).LineNumber);
                    Assert.False(next.IsEnabled);
                    window.NavigateToSourceLine(3);
                    Flatten(window.Roots).Single(node => node.Title == "第三章 继续").Title = "第二章 继续";
                    Assert.Single(suggestions.Items.Cast<object>());
                    var defaults = new TocHierarchyOptions();
                    using var dialogCleanup = new WindowCloser(new NumericHeadingRulesWindow(defaults, defaults, [], false) { Owner = window, ShowInTaskbar = false, Opacity = 0 });
                    var dialog = dialogCleanup.Window;
                    dialog.Show();
                    dialog.UpdateLayout();
                    object Field(string name) => typeof(NumericHeadingRulesWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dialog)!;
                    void Invoke(string name) => typeof(NumericHeadingRulesWindow).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dialog, null);
                    Assert.IsType<TextBox>(Field("_presetName")).Text = "我的数字方案";
                    Assert.IsType<TextBox>(Field("_minimum")).Text = "12";
                    Invoke("SavePreset");
                    Assert.Equal(12, Assert.Single(dialog.Presets).MinimumBodyLines);
                    Assert.IsType<TextBox>(Field("_minimum")).Text = "1";
                    Invoke("LoadPreset");
                    Assert.Equal("12", Assert.IsType<TextBox>(Field("_minimum")).Text);
                    Assert.IsType<ComboBox>(Field("_scope")).SelectedIndex = 2;
                    Assert.Equal("5", Assert.IsType<TextBox>(Field("_minimum")).Text);
                    Assert.False(Assert.IsType<TextBox>(Field("_pattern")).IsEnabled);
                    Assert.Equal(5, defaults.NumericHeadingMinimumBodyLines);
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(5, Assert.IsType<ConversionPreflightIssue>(suggestions.SelectedItem).LineNumber);
                    Flatten(window.Roots).Single(node => node.Title == "第五章 结束").Title = "第三章 结束";
                    Assert.Empty(suggestions.Items.Cast<object>());
                    Assert.False(next.IsEnabled);
                    Assert.IsType<Button>(window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Single(suggestions.Items.Cast<object>());
                }
                catch (Exception exception) { failure = exception; }
                finally { window?.DiscardChangesAndClose(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Promoting_the_selected_first_chapter_does_not_duplicate_its_parent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-promote-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "序\n第一卷\n第1章 开始\n正文\n第2章 继续\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(
                path,
                hierarchy: new TocHierarchyOptions { Enabled = true });
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                ChapterEditorWindow? window = null;
                try
                {
                    window = new ChapterEditorWindow(document)
                    {
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        Opacity = 0,
                    };
                    window.ConfirmAction = _ => true;
                    window.Show();
                    window.UpdateLayout();

                    window.NavigateToSourceLine(4);
                    Assert.Equal(4, Assert.IsType<ChapterTreeSourceLine>(Assert.IsType<ListBox>(window.FindName("SourceLinesList")).SelectedItem).LineNumber);
                    Assert.Null(window.ResultPlan);

                    var tree = Assert.IsType<TreeView>(window.FindName("ChapterTree"));
                    Assert.True(VirtualizingPanel.GetIsVirtualizing(tree));
                    Assert.False(Assert.IsType<CheckBox>(window.FindName("IncludeHtmlTocPageCheck")).IsChecked);
                    Assert.False(Assert.IsType<CheckBox>(window.FindName("IncludeChapterTopNavigationCheck")).IsChecked);
                    var numericCheck = Assert.IsType<CheckBox>(window.FindName("NumericHeadingsCheck"));
                    var numericMinimum = Assert.IsType<TextBox>(window.FindName("NumericMinimumLinesText"));
                    Assert.False(numericCheck.IsChecked);
                    Assert.Equal("5", numericMinimum.Text);
                    Assert.False(numericMinimum.IsEnabled);
                    numericCheck.IsChecked = true;
                    window.UpdateLayout();
                    Assert.True(numericMinimum.IsEnabled);
                    numericCheck.IsChecked = false;
                    var volume = Assert.Single(window.Roots, node => node.Title == "第一卷");
                    var firstChapter = Assert.Single(volume.Children, node => node.Title == "第1章 开始");
                    var volumeContainer = Assert.IsType<TreeViewItem>(
                        tree.ItemContainerGenerator.ContainerFromItem(volume));
                    volumeContainer.IsExpanded = true;
                    window.UpdateLayout();
                    var chapterContainer = Assert.IsType<TreeViewItem>(
                        volumeContainer.ItemContainerGenerator.ContainerFromItem(firstChapter));
                    chapterContainer.IsSelected = true;
                    Assert.Equal(3, window.SelectedLines[0].LineNumber);
                    Assert.Equal("第1章 开始", window.SelectedLines[0].Text);
                    var sourceList = Assert.IsType<ListBox>(window.FindName("SourceLinesList"));
                    sourceList.SelectedItem = window.SelectedLines[0];
                    Assert.False(Assert.IsType<Button>(window.FindName("SplitButton")).IsEnabled);

                    var promote = Assert.IsType<Button>(window.FindName("PromoteButton"));
                    promote.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    var allNodes = Flatten(window.Roots).ToArray();
                    Assert.Equal(4, allNodes.Length);
                    Assert.Equal(allNodes.Length, allNodes.Distinct(ReferenceEqualityComparer.Instance).Count());
                    Assert.Single(window.Roots, node => node.Title == "第一卷");
                    Assert.Single(window.Roots, node => node.Title == "第1章 开始");
                    Assert.Empty(volume.Children);
                    Assert.Equal("第2章 继续", Assert.Single(firstChapter.Children).Title);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window?.DiscardChangesAndClose();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "章节树界面测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Demoting_the_selected_root_uses_the_original_selection_throughout_the_move()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-demote-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第1章 开始\n正文\n第2章 继续\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(
                path,
                hierarchy: new TocHierarchyOptions { Enabled = true });
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                ChapterEditorWindow? window = null;
                try
                {
                    window = new ChapterEditorWindow(document)
                    {
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        Opacity = 0,
                    };
                    window.ConfirmAction = _ => true;
                    window.Show();
                    window.UpdateLayout();

                    var tree = Assert.IsType<TreeView>(window.FindName("ChapterTree"));
                    var firstChapter = Assert.Single(window.Roots, node => node.Title == "第1章 开始");
                    var secondChapter = Assert.Single(window.Roots, node => node.Title == "第2章 继续");
                    var secondContainer = Assert.IsType<TreeViewItem>(
                        tree.ItemContainerGenerator.ContainerFromItem(secondChapter));
                    secondContainer.IsSelected = true;

                    var demote = Assert.IsType<Button>(window.FindName("DemoteButton"));
                    demote.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    var allNodes = Flatten(window.Roots).ToArray();
                    Assert.Equal(3, allNodes.Length);
                    Assert.Equal(allNodes.Length, allNodes.Distinct(ReferenceEqualityComparer.Instance).Count());
                    Assert.Same(firstChapter, Assert.Single(window.Roots, node => !node.IsFrontMatter));
                    Assert.Same(secondChapter, Assert.Single(firstChapter.Children));
                    Assert.Same(firstChapter, secondChapter.Parent);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window?.DiscardChangesAndClose();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "章节树界面测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Chapter_workspace_can_undo_and_redo_a_hierarchy_change()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-undo-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第1章 开始\n正文\n第2章 继续\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(
                path,
                hierarchy: new TocHierarchyOptions { Enabled = true });
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                ChapterEditorWindow? window = null;
                try
                {
                    window = new ChapterEditorWindow(document)
                    {
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        Opacity = 0,
                    };
                    window.ConfirmAction = _ => true;
                    window.Show();
                    window.UpdateLayout();

                    var tree = Assert.IsType<TreeView>(window.FindName("ChapterTree"));
                    var firstChapter = Assert.Single(window.Roots, node => node.Title == "第1章 开始");
                    var secondChapter = Assert.Single(window.Roots, node => node.Title == "第2章 继续");
                    Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(secondChapter)).IsSelected = true;
                    Assert.IsType<Button>(window.FindName("DemoteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Same(secondChapter, Assert.Single(firstChapter.Children));

                    var undo = Assert.IsType<Button>(window.FindName("UndoButton"));
                    Assert.True(undo.IsEnabled);
                    undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(2, window.Roots.Count(node => !node.IsFrontMatter));
                    Assert.Empty(Assert.Single(window.Roots, node => node.Title == "第1章 开始").Children);

                    var redo = Assert.IsType<Button>(window.FindName("RedoButton"));
                    Assert.True(redo.IsEnabled);
                    redo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var restoredFirst = Assert.Single(window.Roots, node => node.Title == "第1章 开始");
                    Assert.Equal("第2章 继续", Assert.Single(restoredFirst.Children).Title);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window?.DiscardChangesAndClose();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "章节工作台撤销测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<ChapterTreeNode> Flatten(IEnumerable<ChapterTreeNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private sealed class WindowCloser(NumericHeadingRulesWindow window) : IDisposable
    {
        public NumericHeadingRulesWindow Window => window;
        public void Dispose() => window.Close();
    }
}
