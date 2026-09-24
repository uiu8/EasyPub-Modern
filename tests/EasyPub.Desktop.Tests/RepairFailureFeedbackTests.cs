using System.IO;
using System.Reflection;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class RepairFailureFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_feedback_checks_actual_bytes_and_blocks_stale_coordinates(bool written)
    {
        var previous = WorkbenchHarness.IsolateCatalogStore();
        try
        {
            var failure = WorkbenchHarness.OnStaThread(() =>
            {
                var path = Path.Combine(Path.GetTempPath(), "easypub-failure-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, "第一章 起点\n正文\n");
                var document = ChapterTreeDocument.Load(path, File.ReadAllBytes(path));
                var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
                try
                {
                    if (written) File.AppendAllText(path, "已写入的内容\n");
                    var bytes = File.ReadAllBytes(path);
                    var text = editor.DescribeIncompleteRepair(document, "目录记录保存失败", "测试备份路径");
                    Assert.Contains(written ? "原文与操作前版本已不同" : "原文仍为操作前版本", text);
                    Assert.DoesNotContain("未应用修复", text);
                    var blocked = (bool)typeof(ChapterEditorWindow).GetField("_sourceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    Assert.Equal(written, blocked);
                    if (written) Assert.Contains("测试备份路径", text);
                    Assert.Equal(bytes, File.ReadAllBytes(path));
                }
                finally { editor.DiscardChangesAndClose(); }
            });
            Assert.Null(failure);
        }
        finally { WorkbenchHarness.RestoreCatalogStore(previous); }
    }
}
