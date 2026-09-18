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
                    var toolbar = Assert.IsType<WrapPanel>(repair.Parent);
                    Assert.Same(repair, toolbar.Children[0]);

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
                    // 主按钮**按本次依据变名**（§5.2），所以不能钉死一句话 —— 只能钉住
                    // "它把依据说出来了"。依据本身由 ChapterWorkbench.RefreshContextBar 填，
                    // 与 ChapterAutoRepair.ReadSavedCatalog 读的是同一处记录。
                    var label = Collect(repair);
                    Assert.True(
                        label.Contains("基于当前章节树检查") || label.Contains("参考目录检查"),
                        $"主按钮没有说明本次依据，实际是：{label}");

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
