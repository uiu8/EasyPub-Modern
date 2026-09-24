using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class RepairBasisVisualTests
{
    [Fact]
    public void Local_basis_retains_catalog_and_renders_with_application_resources()
    {
        var previous = WorkbenchHarness.IsolateCatalogStore();
        var previousSettings = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH",
            Path.Combine(Path.GetTempPath(), "easypub-basis-settings-" + Guid.NewGuid().ToString("N"), "settings.json"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ChapterEditorWindow? window = null;
            try
            {
                var app = new App();
                app.InitializeComponent();
                var path = Path.Combine(Path.GetTempPath(), "easypub-basis-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, "第一章 起点\n正文内容。\n第一章 起点\n第二章 继续\n后续正文。\n");
                var document = ChapterTreeDocument.Load(path, File.ReadAllBytes(path));
                ReferenceCatalogInput.SaveCatalog(document.SourceSha256,
                    ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续")!);
                window = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
                ((CheckBox)window.FindName("LocalRepairOnlyOption")).IsChecked = true;
                Assert.Contains("已保存", WorkbenchHarness.Line(window, "ContextCatalogText"));
                Assert.Contains("不读参考目录", WorkbenchHarness.Line(window, "AutoRepairBasisText"));
                WorkbenchHarness.Save(window, "P1-本地依据保留目录-主题");
            }
            catch (Exception error) { failure = error; }
            finally { window?.DiscardChangesAndClose(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        WorkbenchHarness.RestoreCatalogStore(previous);
        Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", previousSettings);
        Assert.Null(failure);
    }
}
