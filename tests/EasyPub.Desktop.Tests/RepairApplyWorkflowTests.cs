using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class RepairApplyWorkflowTests
{
    [Theory]
    [InlineData(false, RepairLandingMode.TreeOnly, false, false)]
    [InlineData(true, RepairLandingMode.TreeOnly, false, false)]
    [InlineData(false, RepairLandingMode.EditSource, false, false)]
    [InlineData(true, RepairLandingMode.EditSource, false, false)]
    [InlineData(false, RepairLandingMode.EditSource, true, false)]
    [InlineData(true, RepairLandingMode.EditSource, true, false)]
    [InlineData(false, RepairLandingMode.TreeOnly, false, true)]
    [InlineData(true, RepairLandingMode.EditSource, true, true)]
    [InlineData(true, RepairLandingMode.EditSource, true, false, true)]
    [InlineData(false, RepairLandingMode.TreeOnly, false, false, false, true)]
    [InlineData(true, RepairLandingMode.EditSource, false, false, false, true)]
    public async Task Actual_apply_button_honors_source_permission_and_undo_boundary(bool reference, RepairLandingMode mode, bool deleteSource, bool sessionOverride, bool failCatalogSave = false, bool cancelReview = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-apply-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var overrides = new Dictionary<string, string>
        {
            ["EASYPUB_APP_SETTINGS_PATH"] = Path.Combine(root, "settings.json"),
            ["EASYPUB_SOURCE_BACKUPS_PATH"] = Path.Combine(root, "backups"),
            ["EASYPUB_REFERENCE_CATALOGS_PATH"] = Path.Combine(root, "catalogs"),
        };
        var previous = overrides.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var pair in overrides) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await AppSettingsStore.CreateDefault().SaveAsync(EasyPubAppSettings.Default with
                { DefaultRepairLandingMode = sessionOverride
                    ? mode == RepairLandingMode.TreeOnly ? RepairLandingMode.EditSource : RepairLandingMode.TreeOnly
                    : mode });
            var path = Path.Combine(root, "book.txt");
            const string text = "第一章 起点\n这是一段必须完整保留的正文，只处理重复标题，不能在没有许可时删除正文或改动原始文件。\n第一章 起点\n第二章 继续\n后续正文\n";
            File.WriteAllText(path, text);
            var bytes = File.ReadAllBytes(path);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ChapterEditorWindow? editor = null;
                DispatcherTimer? timer = null;
                try
                {
                    var document = ChapterTreeDocument.Load(path, bytes);
                    var localWithSavedCatalog = sessionOverride && !reference;
                    if (localWithSavedCatalog)
                        ReferenceCatalogInput.SaveCatalog(document.SourceSha256,
                            ReferenceCatalogInput.ParseText("第一章 目录中的不同标题\n第二章 目录中的另一标题")!);
                    var catalogFiles = Directory.Exists(overrides["EASYPUB_REFERENCE_CATALOGS_PATH"])
                        ? Directory.GetFiles(overrides["EASYPUB_REFERENCE_CATALOGS_PATH"], "*", SearchOption.AllDirectories)
                            .ToDictionary(p => p, File.ReadAllBytes) : new Dictionary<string, byte[]>();
                    editor = new ChapterEditorWindow(document) { ShowInTaskbar = false, Left = -4000, Top = -4000 };
                    editor.Show(); editor.UpdateLayout();
                    if (sessionOverride && reference)
                        ((CheckBox)editor.FindName("LocalRepairOnlyOption")).IsChecked = true;
                    if (localWithSavedCatalog)
                    {
                        Assert.Contains("按目录", WorkbenchHarness.Line(editor, "AutoRepairButtonText"));
                        var localOption = (CheckBox)editor.FindName("LocalRepairOnlyOption");
                        localOption.IsChecked = true;
                        Assert.DoesNotContain("按目录", WorkbenchHarness.Line(editor, "AutoRepairButtonText"));
                        Assert.Contains("不读参考目录", WorkbenchHarness.Line(editor, "AutoRepairBasisText"));
                        localOption.IsChecked = false;
                        Assert.Contains("按目录", WorkbenchHarness.Line(editor, "AutoRepairButtonText"));
                        localOption.IsChecked = true;
                    }
                    if (sessionOverride)
                    {
                        // Model a mode already accepted in this workbench. A new review must
                        // use it, even though the global preference still says the opposite.
                        typeof(ChapterEditorWindow).GetField("_landingModeCache", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(editor, (RepairLandingMode?)mode);
                        typeof(ChapterEditorWindow).GetMethod("UpdateSaveState", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(editor, null);
                        Assert.Contains(mode == RepairLandingMode.TreeOnly ? "只改章节树" : "改原文",
                            WorkbenchHarness.Line(editor, "SaveStateText"));
                    }
                    var originalTitles = editor.Roots.Select(n => n.Title).ToArray();
                    var clicked = false;
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    timer.Tick += (_, _) =>
                    {
                        var dialog = editor.OwnedWindows.OfType<RepairReviewWindow>().FirstOrDefault();
                        if (dialog is null) return;
                        timer.Stop();
                        try
                        {
                            Assert.Equal(mode, dialog.LandingMode);
                            if (sessionOverride && reference)
                                Assert.False(((CheckBox)editor.FindName("LocalRepairOnlyOption")).IsChecked == true);
                            if (localWithSavedCatalog)
                            {
                                var actual = (AutoRepairOutcome)typeof(RepairReviewWindow)
                                    .GetField("_outcome", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                                Assert.False(actual.CatalogFound);
                            }
                            var duplicate = Descendants<CheckBox>(dialog).Single(box =>
                                box.Tag is string key && key.StartsWith("RemoveDuplicate|", StringComparison.Ordinal));
                            duplicate.IsChecked = true;
                            if (deleteSource)
                                Descendants<CheckBox>(dialog).Single(box => Equals(box.Content, "并从原文删除这一份")).IsChecked = true;
                            Assert.Equal(deleteSource, dialog.CurrentPreview!.SourceWillChange);
                            Assert.Contains(dialog.SelectedActions(), a => a.Kind == ReferenceActionKind.RemoveDuplicate);
                            var apply = Descendants<Button>(dialog).Single(b => Equals(b.Content, "应用所选修改"));
                            Assert.True(apply.IsEnabled);
                            if (cancelReview)
                            {
                                Descendants<RadioButton>(dialog).Single(r => Equals(r.Content,
                                    mode == RepairLandingMode.TreeOnly ? "同时改原文" : "只改章节树")).IsChecked = true;
                                Descendants<Button>(dialog).Single(b => Equals(b.Content, "暂不应用"))
                                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                // IsCancel uses WPF command handling for mouse/keyboard; explicitly
                                // close if a synthetic routed event did not invoke that command.
                                if (dialog.IsVisible) dialog.DialogResult = false;
                                clicked = true;
                                return;
                            }
                            if (failCatalogSave)
                            {
                                // A file cannot act as a directory. The catalog write after
                                // successful source replacement must fail on the real filesystem.
                                var blocker = Path.Combine(root, "catalog-write-blocker");
                                File.WriteAllText(blocker, "intentional test fixture");
                                Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", blocker);
                            }
                            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            clicked = true;
                        }
                        catch (Exception error) { failure = error; dialog.Close(); }
                    };
                    timer.Start();
                    var catalog = reference ? ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续") : null;
                    var task = (Task)typeof(ChapterEditorWindow).GetMethod("RunRepairReviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, [catalog, (Action<string>)(_ => { })])!;
                    while (!task.IsCompleted && DateTime.UtcNow < deadline)
                        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Assert.True(task.IsCompleted, "修复入口没有结束");
                    task.GetAwaiter().GetResult();
                    if (failure is not null) throw failure;
                    Assert.True(clicked, "没有点击真实应用按钮");
                    if (cancelReview)
                    {
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                        Assert.Equal(originalTitles, editor.Roots.Select(n => n.Title));
                        Assert.False(((Button)editor.FindName("UndoButton")).IsEnabled);
                        var effective = typeof(ChapterEditorWindow).GetProperty("LandingMode", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(editor);
                        Assert.Equal(mode, effective);
                        Assert.Empty(SourceBackupStore.CreateDefault().Inventory().ListEntries(path)
                            .Where(b => b.Kind == SourceBackupKind.Snapshot));
                        return;
                    }
                    if (failCatalogSave)
                    {
                        Assert.NotEqual(bytes, File.ReadAllBytes(path));
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var message = (string)typeof(ChapterEditorWindow).GetField("_resultMessage", flags)!.GetValue(editor)!;
                        Assert.Contains("原文与操作前版本已不同", message);
                        Assert.DoesNotContain("未应用修复", message);
                        Assert.True((bool)typeof(ChapterEditorWindow).GetField("_sourceChanged", flags)!.GetValue(editor)!);
                        Assert.Equal(originalTitles, editor.Roots.Select(n => n.Title));
                        var originalBackup = SourceBackupStore.CreateDefault().Inventory().ListEntries(path)
                            .First(b => b.Sha256 == document.SourceSha256 && b.HasSavedTree);
                        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", overrides["EASYPUB_REFERENCE_CATALOGS_PATH"]);
                        var restored = new RestoreTransaction(Path.Combine(root, "restore-transactions"), path)
                            .ExecuteAsync(SourceTransitionState.LoadFromAsync(path).GetAwaiter().GetResult(),
                                originalBackup.Path, backupDestination: Path.Combine(root, "before-restore.bak"))
                            .GetAwaiter().GetResult();
                        Assert.True(restored.Succeeded, restored.Message);
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                        var recheck = editor.CheckSourceVersionAsync();
                        while (!recheck.IsCompleted && DateTime.UtcNow < deadline)
                            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                        Assert.True(recheck.IsCompleted);
                        recheck.GetAwaiter().GetResult();
                        Assert.False((bool)typeof(ChapterEditorWindow).GetField("_sourceChanged", flags)!.GetValue(editor)!);
                        return;
                    }
                    if (localWithSavedCatalog)
                        foreach (var saved in catalogFiles) Assert.Equal(saved.Value, File.ReadAllBytes(saved.Key));
                    Assert.Single(editor.Roots, n => n.Title == "第一章 起点");
                    // ChapterTreeDocument counts the empty logical line after a final newline.
                    var onDiskLines = File.ReadAllText(path).Split('\n');
                    var coveredText = editor.Roots.SelectMany(node => node.ContentRanges)
                        .SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1))
                        .Select(line => onDiskLines[line - 1]).ToArray();
                    Assert.Contains(text.Split('\n')[1], coveredText);
                    Assert.Contains("后续正文", coveredText);
                    var undo = (Button)editor.FindName("UndoButton");
                    if (deleteSource)
                    {
                        Assert.NotEqual(bytes, File.ReadAllBytes(path));
                        Assert.Equal(1, File.ReadAllLines(path).Count(line => line == "第一章 起点"));
                        Assert.Contains("这是一段必须完整保留的正文", File.ReadAllText(path));
                        Assert.False(undo.IsEnabled);
                        Assert.Equal("正常", ((TextBlock)editor.FindName("ContextSourceText")).Text);
                    }
                    else
                    {
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                        Assert.True(undo.IsEnabled);
                        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(originalTitles, editor.Roots.Select(n => n.Title));
                        Assert.Equal("本地识别", ((TextBlock)editor.FindName("ContextTreeText")).Text);
                        Assert.NotEmpty(Directory.GetFiles(Path.Combine(overrides["EASYPUB_SOURCE_BACKUPS_PATH"], "Removed"), "*", SearchOption.AllDirectories));
                    }
                    Assert.NotEmpty(Directory.GetFiles(overrides["EASYPUB_SOURCE_BACKUPS_PATH"], "*", SearchOption.AllDirectories));
                    var backups = SourceBackupStore.CreateDefault().Inventory().ListEntries(path);
                    Assert.Contains(backups, backup => backup.Sha256 == document.SourceSha256 && backup.HasSavedTree);
                }
                catch (Exception error) { failure = error; }
                finally { timer?.Stop(); editor?.DiscardChangesAndClose(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI 测试超时");
            Assert.Null(failure);
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            // Keep isolated book, backups and receipts as evidence; never use user storage.
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
