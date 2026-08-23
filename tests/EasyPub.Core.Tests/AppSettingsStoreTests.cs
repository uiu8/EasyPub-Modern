using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public async Task Last_profile_and_named_presets_are_restored_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-settings-{Guid.NewGuid():N}");
        var storagePath = Path.Combine(root, "settings.json");
        var profile = new ConversionProfile(
            "mobi",
            "作者",
            3,
            @"C:\styles\novel.css",
            new ConversionOptions
            {
                FontSizePercent = 125,
                LineHeightPercent = 150,
                Mobi = new MobiOptions { Compression = MobiCompression.High },
            });
        var settings = new EasyPubAppSettings(
            @"D:\电子书",
            profile,
            [new NamedConversionPreset("Kindle 大字版", profile)]);

        try
        {
            await new AppSettingsStore(storagePath).SaveAsync(settings);
            var restored = await new AppSettingsStore(storagePath).LoadAsync();

            Assert.Equal(@"D:\电子书", restored.OutputDirectory);
            Assert.Equal(125, restored.LastProfile.FontSizePercent);
            Assert.Equal(MobiCompression.High, restored.LastProfile.MobiCompression);
            Assert.Equal("Kindle 大字版", Assert.Single(restored.Presets).Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Synchronous_close_save_does_not_deadlock_a_non_pumping_ui_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-close-save-regression");
        Directory.CreateDirectory(root);
        var store = new AppSettingsStore(Path.Combine(root, "settings.json"));
        var largeAuthor = new string('作', 4_000_000);
        var settings = new EasyPubAppSettings(
            @"D:\电子书",
            ConversionProfile.Default with { Author = largeAuthor },
            []);
        using var finished = new ManualResetEventSlim(false);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                store.SaveAsync(settings).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                finished.Set();
            }
        }) { IsBackground = true };

        thread.Start();
        var completed = finished.Wait(TimeSpan.FromSeconds(2));

        Assert.True(completed, "关闭时保存设置在 UI 同步上下文中发生死锁。");
        Assert.Null(error);
        Directory.Delete(root, recursive: true);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // A closing WPF dispatcher cannot process an awaited continuation.
        }
    }
}
