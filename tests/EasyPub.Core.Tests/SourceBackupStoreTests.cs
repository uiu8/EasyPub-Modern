using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class SourceBackupStoreTests
{
    [Fact]
    public void Repeated_edit_preserves_first_original_and_does_not_create_more_backups()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easypub-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "小说.txt");
            File.WriteAllText(source, "第一版原文");
            var store = new SourceBackupStore(Path.Combine(dir, "backups"));
            var backup = store.EnsureBackup(source);
            File.WriteAllText(source, "第二版原文");
            Assert.Equal(backup, store.EnsureBackup(source));
            Assert.Equal("第一版原文", File.ReadAllText(backup));
            Assert.Single(store.FindBackups(source));
            Assert.Empty(Directory.GetFiles(dir, "*.bak"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void Same_filename_in_different_folders_has_independent_backup_and_legacy_files_are_only_listed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easypub-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "other"));
        try
        {
            var a = Path.Combine(dir, "book.txt"); var b = Path.Combine(dir, "other", "book.txt");
            File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
            var store = new SourceBackupStore(Path.Combine(dir, "backups"));
            Assert.NotEqual(store.EnsureBackup(a), store.EnsureBackup(b));
            var legacy = a + "." + Guid.NewGuid().ToString("N") + ".bak";
            File.WriteAllText(legacy, "old");
            File.WriteAllText(a + ".bak", "unrelated");
            Assert.Equal(2, store.FindBackups(a).Count);
            Assert.Equal("old", File.ReadAllText(legacy));
            Assert.Single(store.FindBackups(b));
        }
        finally { Directory.Delete(dir, true); }
    }
}
