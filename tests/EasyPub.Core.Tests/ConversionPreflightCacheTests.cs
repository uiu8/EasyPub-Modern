using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ConversionPreflightCacheTests
{
    [Fact]
    public async Task Reuses_result_until_an_input_or_option_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-preflight-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "book.txt");
        await File.WriteAllTextAsync(input, "第一章 雨夜\n正文");
        var request = new ConversionRequest(input, Path.Combine(root, "book.epub"), Options: new ConversionOptions());
        var report = new ConversionPreflightReport([new ConversionPreflightBook(input, 1)], []);
        var cache = new ConversionPreflightCache();

        try
        {
            cache.Store([request], report);
            Assert.True(cache.TryGet([request], out var restored));
            Assert.Same(report, restored);

            var changedOption = request with { Options = request.Options! with { FontSizePercent = 125 } };
            Assert.False(cache.TryGet([changedOption], out _));

            await File.AppendAllTextAsync(input, "\n第二行");
            Assert.False(cache.TryGet([request], out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
