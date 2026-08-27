using System.IO;
using System.Text;

namespace EasyPub.Desktop.Tests;

public sealed class TextPreviewCacheTests
{
    [Fact]
    public async Task Reuses_analysis_until_the_source_file_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-preview-cache-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 初见\r\n正文一", Encoding.UTF8);
            var cache = new TextPreviewCache();

            var first = await cache.GetAsync(path, null);
            var second = await cache.GetAsync(path, null);

            Assert.Same(first, second);
            Assert.Contains("正文一", first.PreviewText);

            await Task.Delay(20);
            await File.WriteAllTextAsync(path, "第一章 重逢\r\n正文二更长", Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            var changed = await cache.GetAsync(path, null);

            Assert.NotSame(first, changed);
            Assert.Contains("正文二更长", changed.PreviewText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancellation_does_not_discard_a_shared_analysis()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-preview-cancel-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 测试\r\n正文", Encoding.UTF8);
            var cache = new TextPreviewCache();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(path, null, cancellation.Token));
            var snapshot = await cache.GetAsync(path, null);

            Assert.Contains("第一章 测试", snapshot.PreviewText);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
