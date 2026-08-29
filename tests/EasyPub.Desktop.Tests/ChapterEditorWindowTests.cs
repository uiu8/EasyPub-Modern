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
                    window.Show();
                    window.UpdateLayout();

                    var tree = Assert.IsType<TreeView>(window.FindName("ChapterTree"));
                    Assert.True(VirtualizingPanel.GetIsVirtualizing(tree));
                    Assert.False(Assert.IsType<CheckBox>(window.FindName("IncludeHtmlTocPageCheck")).IsChecked);
                    var volume = Assert.Single(window.Roots, node => node.Title == "第一卷");
                    var firstChapter = Assert.Single(volume.Children, node => node.Title == "第1章 开始");
                    var volumeContainer = Assert.IsType<TreeViewItem>(
                        tree.ItemContainerGenerator.ContainerFromItem(volume));
                    volumeContainer.IsExpanded = true;
                    window.UpdateLayout();
                    var chapterContainer = Assert.IsType<TreeViewItem>(
                        volumeContainer.ItemContainerGenerator.ContainerFromItem(firstChapter));
                    chapterContainer.IsSelected = true;

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
                    window?.Close();
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
                    window?.Close();
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
                    window?.Close();
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
}
