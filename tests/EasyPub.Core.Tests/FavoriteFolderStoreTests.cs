using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class FavoriteFolderStoreTests
{
    [Fact]
    public async Task Favorite_folders_are_deduplicated_and_restored_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-favorites-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "第一书库");
        var second = Path.Combine(root, "第二书库");
        var storagePath = Path.Combine(root, "settings", "favorite-folders.json");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            var store = new FavoriteFolderStore(storagePath);
            await store.AddAsync(first);
            await store.AddAsync(second);
            await store.AddAsync(first + Path.DirectorySeparatorChar);

            var restored = await new FavoriteFolderStore(storagePath).LoadAsync();

            Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], restored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_deletes_only_the_favorite_record_and_persists_the_change()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-favorites-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "保留书库");
        var second = Path.Combine(root, "取消收藏书库");
        var storagePath = Path.Combine(root, "settings", "favorite-folders.json");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            var store = new FavoriteFolderStore(storagePath);
            await store.AddAsync(first);
            await store.AddAsync(second);

            await store.RemoveAsync(second + Path.DirectorySeparatorChar);
            var restored = await new FavoriteFolderStore(storagePath).LoadAsync();

            Assert.Equal([Path.GetFullPath(first)], restored);
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
