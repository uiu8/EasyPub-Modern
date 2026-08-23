using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ConversionHistoryStoreTests
{
    [Fact]
    public async Task History_entries_are_appended_and_restored_in_newest_first_order()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-history-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "history.json");
        var older = new ConversionHistoryEntry(
            Guid.NewGuid(), new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero),
            "a.txt", "a.epub", true, 10, 1024, 1200, null);
        var newer = new ConversionHistoryEntry(
            Guid.NewGuid(), new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero),
            "b.txt", "b.mobi", false, null, null, null, "转换失败");

        try
        {
            var store = new ConversionHistoryStore(path);
            await store.AppendAsync([older]);
            await store.AppendAsync([newer]);

            var restored = await new ConversionHistoryStore(path).LoadAsync();

            Assert.Equal([newer.Id, older.Id], restored.Select(entry => entry.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
