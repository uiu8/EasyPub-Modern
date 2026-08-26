using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class WorkflowPolicyTests
{
    [Fact]
    public void Auto_concurrency_is_resource_aware_and_not_capped_at_four()
    {
        var epubJobs = Enumerable.Range(0, 20).Select(index => Request($"book-{index}.epub")).ToArray();
        var mobiJobs = Enumerable.Range(0, 20).Select(index => Request($"book-{index}.mobi")).ToArray();

        Assert.Equal(16, ConversionConcurrencyPolicy.Resolve(0, epubJobs, logicalProcessors: 24));
        Assert.Equal(8, ConversionConcurrencyPolicy.Resolve(0, mobiJobs, logicalProcessors: 24));
        Assert.Equal(12, ConversionConcurrencyPolicy.Resolve(12, mobiJobs, logicalProcessors: 4));
    }

    [Fact]
    public void Output_collision_can_rename_overwrite_or_skip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "book.mobi");
        File.WriteAllText(path, "old");
        try
        {
            Assert.EndsWith("book (2).mobi", OutputPathPolicy.Resolve(path, OutputCollisionPolicy.AutoRename).Path);
            Assert.Equal(path, OutputPathPolicy.Resolve(path, OutputCollisionPolicy.Overwrite).Path);
            Assert.True(OutputPathPolicy.Resolve(path, OutputCollisionPolicy.Skip).Skipped);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Paused_execution_control_waits_until_resume()
    {
        var control = new BatchExecutionControl();
        control.Pause();
        var waiting = control.WaitIfPausedAsync();
        Assert.False(waiting.IsCompleted);
        control.Resume();
        await waiting;
    }

    [Fact]
    public void Kindle_catalog_contains_kpw3_through_kpw6_and_custom_profile()
    {
        Assert.All(new[] { "kpw3", "kpw4", "kpw5", "kpw6" }, id =>
            Assert.Contains(KindleDeviceProfiles.BuiltIn, profile => profile.Id == id));
        var custom = KindleDeviceProfiles.Custom(1404, 1872, 300);
        Assert.Equal(1404, custom.PixelWidth);
        Assert.Equal(1872, custom.PixelHeight);
    }

    private static ConversionRequest Request(string outputPath) => new(
        "input.txt", outputPath, "Book", null, ConversionOptions.LegacyDefault);
}
