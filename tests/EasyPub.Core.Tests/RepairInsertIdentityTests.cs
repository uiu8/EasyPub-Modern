using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class RepairInsertIdentityTests
{
    [Fact]
    public void Two_actions_insert_headings_with_distinct_replayable_coordinates()
    {
        var original = SourceTextDocument.Parse("甲正文\n乙正文\n");
        RepairEffect Effect(int line, string title) => new(new TreeEffectInsertEntry(title, 1),
            new SourceContentInsert([new InsertIntent(new BeforeOriginalLine(line), title)]), new BodyOwnershipUnchanged());
        var first = RepairSourcePatchCompiler.Project("chapter-a", Effect(1, "第一章 甲"));
        var second = RepairSourcePatchCompiler.Project("chapter-b", Effect(2, "第二章 乙"));
        var operations = first.Concat(second).ToArray();
        Assert.Equal(2, operations.Select(op => op.OperationId).Distinct().Count());
        Assert.Equal(first, RepairSourcePatchCompiler.Project("chapter-a", Effect(1, "第一章 甲")));
        var patch = new SourcePatch("two-inserts", "fixture", operations);
        var rendered = SourcePatchRenderer.Render(original, patch);
        var map = SourceCoordinateMap.Build(original, patch, rendered.Text);
        Assert.Equal(1, map.LineOfOperation(first[0].OperationId));
        Assert.Equal(3, map.LineOfOperation(second[0].OperationId));
        Assert.Equal("第一章 甲\n甲正文\n第二章 乙\n乙正文\n", rendered.Text);
    }
}
