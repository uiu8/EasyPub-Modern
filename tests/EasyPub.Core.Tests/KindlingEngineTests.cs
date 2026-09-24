using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class KindlingEngineTests
{
    [Theory]
    [InlineData("txt", EpubInputMode.PreserveOriginal)]
    [InlineData("epub", EpubInputMode.PreserveOriginal)]
    [InlineData("epub", EpubInputMode.EasyPubCompatible)]
    public async Task Real_engine_converts_without_KindleGen_and_preserves_input(string type, EpubInputMode mode)
    {
        var engine = Environment.GetEnvironmentVariable("EASYPUB_TEST_KINDLING");
        if (string.IsNullOrWhiteSpace(engine)) return; // Opt-in real executable smoke test.
        var folder = Path.Combine(Path.GetTempPath(), "easypub-kindling-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var txt = Path.Combine(folder, "中文 原稿.txt");
        await File.WriteAllTextAsync(txt, "第一章 起点\n春风吹过庭院，他打开一封远方寄来的信。\n第二章 归途\n山路蜿蜒，灯火在夜色中渐渐亮起。\n");
        var converter = new EasyPubConverter();
        var input = txt;
        if (type == "epub")
        {
            input = Path.Combine(folder, "书稿.epub");
            await converter.ConvertAsync(new(txt, input, Options: ConversionOptions.LegacyDefault));
        }
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        var options = ConversionOptions.LegacyDefault with { Mobi = new MobiOptions
        { Engine = KindleConversionEngine.Kindling, KindlingPath = engine, EpubInputMode = mode } };
        var saved = JsonSerializer.Deserialize<ConversionOptions>(JsonSerializer.Serialize(options))!;
        Assert.Equal(KindleConversionEngine.Kindling, saved.Mobi.Engine);
        var request = new ConversionRequest(input, Path.Combine(folder, "成品.mobi"), Options: saved);
        if (type == "txt")
        {
            var tree = await ChapterTreeDocument.LoadAsync(txt);
            request = request with { ChapterTree = tree.CreatePlan(tree.Entries) };
        }
        var check = await new ConversionPreflightInspector().InspectAsync([request]);
        Assert.DoesNotContain(check.Issues, i => i.Code is "kindlegen_missing" or "kindling_missing");
        var result = await converter.ConvertAsync(request);
        Assert.True(result.OutputBytes > 1024);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(request.OutputPath), 60, 8));
        Assert.Single(Directory.GetFiles(folder, "*.log"));
    }

    [Fact]
    public void Old_settings_keep_KindleGen_as_default() =>
        Assert.Equal(KindleConversionEngine.KindleGen, JsonSerializer.Deserialize<MobiOptions>("{}")!.Engine);
}
