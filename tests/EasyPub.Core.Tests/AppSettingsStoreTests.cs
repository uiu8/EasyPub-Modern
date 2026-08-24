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
                ArtifactValidation = new ArtifactValidationOptions { Enabled = true, MaxReportCount = 20 },
            });
        var settings = new EasyPubAppSettings(
            @"D:\电子书",
            profile,
            [new NamedConversionPreset("Kindle 大字版", profile)])
        {
            UseLegacyConfig = false,
            LegacyConfigPath = null,
        };

        try
        {
            await new AppSettingsStore(storagePath).SaveAsync(settings);
            var restored = await new AppSettingsStore(storagePath).LoadAsync();

            Assert.Equal(@"D:\电子书", restored.OutputDirectory);
            Assert.Equal(125, restored.LastProfile.FontSizePercent);
            Assert.Equal(MobiCompression.High, restored.LastProfile.MobiCompression);
            Assert.True(restored.LastProfile.Options.ArtifactValidation.Enabled);
            Assert.Equal(20, restored.LastProfile.Options.ArtifactValidation.MaxReportCount);
            Assert.Equal("Kindle 大字版", Assert.Single(restored.Presets).Name);
            Assert.False(restored.UseLegacyConfig);
            Assert.Null(restored.LegacyConfigPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_written_before_config_selection_was_added_keep_automatic_config_enabled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-old-settings-{Guid.NewGuid():N}");
        var storagePath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(storagePath,
                """
                {
                  "OutputDirectory": null,
                  "LastProfile": {
                    "OutputFormat": "epub",
                    "Author": null,
                    "Parallelism": 1,
                    "AdditionalCssFilePath": null,
                    "Options": {}
                  },
                  "Presets": []
                }
                """);

            var restored = await new AppSettingsStore(storagePath).LoadAsync();

            Assert.True(restored.UseLegacyConfig);
            Assert.Null(restored.LegacyConfigPath);
            Assert.False(restored.LastProfile.Options.ArtifactValidation.Enabled);
            Assert.Equal(10, restored.LastProfile.Options.ArtifactValidation.MaxReportCount);
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
