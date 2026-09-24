using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class ChapterSplitCatalogTests
{
    [Fact]
    public void Catalog_selection_is_explicit_filterable_and_keeps_boundary_unchanged()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ChapterSplitWindow? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                var path = Path.Combine(Path.GetTempPath(), "split-catalog-" + Guid.NewGuid().ToString("N") + ".txt");
                var text = "第一章 起点\n清晨，他推开院门，望向远处的山。\n这是前一章的结尾。\n归途从这里开始，行囊里装着一封信。\n她在路口等候，两人重新踏上旅程。";
                File.WriteAllText(path, text);
                var doc = ChapterTreeDocument.Load(path, File.ReadAllBytes(path));
                var entry = doc.Entries.Single(e => !e.IsFrontMatter);
                var split = ChapterContentSplit.At(entry.ContentRanges, 4);
                Assert.NotNull(split);
                var catalog = new ReferenceCatalog("手动导入的六卷目录", "样书", [
                    new("第一卷", ReferenceNodeKind.Volume, null, null),
                    new("第二章 归途", ReferenceNodeKind.Chapter, null, "第一卷"),
                    new("第二章 归途", ReferenceNodeKind.Chapter, null, "第二卷"),
                    new("第三章 山门", ReferenceNodeKind.Chapter, null, "第二卷")]);
                window = new ChapterSplitWindow(doc, entry.Title, split, catalog) { Style = null,
                    ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                window.Show(); window.UpdateLayout();
                Assert.Equal(3, window.CatalogTitles.Items.Count);
                Assert.Equal(-1, window.CatalogTitles.SelectedIndex);
                Assert.False(window.UseCatalogTitle.IsEnabled);
                Assert.False(window.ConfirmButton.IsEnabled);
                window.TitleInput.Text = "我的手工标题";
                window.CatalogSearch.Text = "第二卷";
                Assert.Equal(2, window.CatalogTitles.Items.Count);
                window.CatalogTitles.SelectedIndex = 0;
                Assert.Equal("第二卷 · 第二章 归途", window.CatalogTitles.SelectedItem.ToString());
                Assert.Equal("我的手工标题", window.ChapterTitle);
                window.UseCatalogTitle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("第二章 归途", window.ChapterTitle);
                Assert.True(window.ConfirmButton.IsEnabled);
                Assert.False(window.ExportCopyOption.IsChecked == true);
                WorkbenchHarness.Save(window, "人工切分选择目录标题");
                window.CatalogSearch.Text = "不存在的标题";
                Assert.Empty(window.CatalogTitles.Items.Cast<object>());
                Assert.False(window.UseCatalogTitle.IsEnabled);
                Assert.Equal("第二章 归途", window.ChapterTitle);
                Assert.Equal(4, split.After[0].StartLine);
                Assert.Equal(text, File.ReadAllText(path));
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }
}
