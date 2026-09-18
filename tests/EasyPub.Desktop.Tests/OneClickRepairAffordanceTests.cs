using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// The one-click repair button is the action most users want and it used to be indistinguishable from
/// its neighbours. These lock the affordance so a future layout pass cannot quietly flatten it again.
/// The colour is checked through the style's resource key rather than the resolved brush, because a
/// window constructed without App.xaml (which is how these tests run) has no resource dictionary.
/// </summary>
public class OneClickRepairAffordanceTests
{
    [Fact]
    public async Task One_click_repair_comes_first_and_carries_the_primary_colour()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-repair-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第1章 开始\n正文\n第2章 继续\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new ChapterEditorWindow(document)
                    {
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        Opacity = 0,
                    };
                    window.Show();
                    window.UpdateLayout();

                    var repair = Assert.IsType<Button>(window.FindName("AutoRepairButton"));
                    // 主按钮下面挂着一行依据（AutoRepairBasisText），所以它先属于一个竖排 StackPanel，
                    // 那个 StackPanel 才是工具栏的第一项 —— 一行六个控件因此排得下。
                    var holder = Assert.IsType<StackPanel>(repair.Parent);
                    var toolbar = Assert.IsType<WrapPanel>(holder.Parent);
                    Assert.Same(holder, toolbar.Children[0]);

                    var style = Assert.IsType<Style>(repair.Style);
                    string? KeyFor(DependencyProperty property) =>
                        style.Setters.OfType<Setter>()
                            .FirstOrDefault(setter => setter.Property == property)?.Value is DynamicResourceExtension resource
                            ? resource.ResourceKey?.ToString()
                            : null;
                    Assert.Equal("PrimaryActionBrush", KeyFor(Control.BackgroundProperty));
                    Assert.Equal("PrimaryActionBrush", KeyFor(Control.BorderBrushProperty));
                    Assert.Equal("PrimaryActionTextBrush", KeyFor(Control.ForegroundProperty));
                    Assert.True(repair.MinHeight >= 40, $"一键修复高度只有 {repair.MinHeight}");
                    Assert.True(repair.FontSize >= 15, $"一键修复字号只有 {repair.FontSize}");
                    Assert.Equal(FontWeights.SemiBold, repair.FontWeight);
                    // 按钮**只说做什么**，依据由旁边那一行 AutoRepairBasisText 承担（§5.6）：
                    // 整句依据塞进按钮里，这一行六个控件就排不下，而按钮名越长越没人读。
                    var label = Collect(repair);
                    Assert.Contains("检查并生成建议", label);

                    window.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
            if (failure is not null) throw failure;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The button's caption lives in a StackPanel so it can carry an icon; read it out.</summary>
    private static string Collect(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(block => block.Text)),
        _ => "",
    };
}
