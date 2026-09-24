using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class SixVolumeLocationOwnershipTests
{
    [Fact]
    public async Task Located_titles_belong_to_their_actual_bodies_and_missing_titles_stay_unlocated()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "六卷690章修复验收")))
            root = root.Parent;
        Assert.NotNull(root);
        var folder = Path.Combine(root!.FullName, "samples", "六卷690章修复验收");
        var lines = await File.ReadAllLinesAsync(Path.Combine(folder, "缺陷样书.txt"));
        // The fixture marks volume headings with Markdown; use the same explicit adapter as the baseline tool.
        var catalogText = string.Join('\n', (await File.ReadAllLinesAsync(Path.Combine(folder, "参考目录.txt")))
            .Select(line => line.StartsWith("# ") ? line[2..] : line));
        var catalog = ReferenceCatalogInput.ParseText(catalogText)!;
        using var truth = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "真值清单.json")));
        var titles = truth.RootElement.GetProperty("chapters").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("title").GetString()!);
        var instances = truth.RootElement.GetProperty("instances").EnumerateArray().ToArray();
        var location = ReferenceLocator.Locate(lines, catalog);
        Assert.Equal(668, location.Found);
        Assert.Equal(22, location.Missing);
        foreach (var chapter in location.Chapters)
        {
            if (chapter.Line is not int line)
            {
                Assert.Equal("第4卷 旅程4", chapter.Volume);
                var number = int.Parse(ReferenceOutline.ParseKey(chapter.Reference.Title).Number);
                Assert.True(number is >= 30 and <= 40 or >= 50 and <= 60);
                continue;
            }
            var instance = Assert.Single(instances.Where(i => i.GetProperty("start_line").GetInt32() <= line
                && i.GetProperty("body_start_line").GetInt32() >= line));
            Assert.Equal(titles[instance.GetProperty("id").GetString()!], chapter.Reference.Title);
        }
    }
}
