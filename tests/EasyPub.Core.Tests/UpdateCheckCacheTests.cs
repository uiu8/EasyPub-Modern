using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class UpdateCheckCacheStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(8));

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "easypub-checkcache-" + Guid.NewGuid().ToString("N"), "update-check.json");

    private static void CleanUp(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Round_trips_a_check_result()
    {
        var path = TempPath();
        try
        {
            var store = new UpdateCheckCacheStore(path);
            Assert.Null(store.Load());

            store.Save(new UpdateCheckCache(Now, "v1.57.1"));
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("v1.57.1", loaded!.LatestTag);
            Assert.Equal(Now, loaded.CheckedAt);
            Assert.True(loaded.HasNewerRelease);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Up_to_date_result_is_kept_with_an_empty_tag()
    {
        var path = TempPath();
        try
        {
            var store = new UpdateCheckCacheStore(path);
            store.Save(new UpdateCheckCache(Now, string.Empty));
            Assert.False(store.Load()!.HasNewerRelease);
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Fact]
    public void Corrupt_record_reads_as_null_and_only_costs_one_extra_request()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ 不是 json");
            var store = new UpdateCheckCacheStore(path);
            Assert.Null(store.Load());
            // 读不出来就等于没有缓存，于是照常去查一次——不能因为文件坏了就不再检查更新。
            Assert.False(UpdateCheckCacheStore.IsFresh(store.Load(), Now, UpdateCheckCacheStore.DefaultInterval));
        }
        finally
        {
            CleanUp(path);
        }
    }

    [Theory]
    [InlineData(0, true)]        // 刚查过
    [InlineData(1, true)]
    [InlineData(5, true)]        // 间隔内
    [InlineData(6, false)]       // 正好到期
    [InlineData(48, false)]      // 早就过期
    public void Freshness_follows_the_interval(int hoursAgo, bool expected)
    {
        var cache = new UpdateCheckCache(Now.AddHours(-hoursAgo), "v1.57.1");
        Assert.Equal(expected, UpdateCheckCacheStore.IsFresh(cache, Now, UpdateCheckCacheStore.DefaultInterval));
    }

    [Fact]
    public void Missing_cache_is_never_fresh() =>
        Assert.False(UpdateCheckCacheStore.IsFresh(null, Now, UpdateCheckCacheStore.DefaultInterval));

    [Theory]
    [InlineData(1)]     // 轻微超前：容忍（时钟微调）
    [InlineData(30)]    // 大幅超前：说明系统时间被改过，不能再信这条记录
    public void Timestamp_from_the_future_is_not_trusted_beyond_a_small_tolerance(int minutesAhead)
    {
        var cache = new UpdateCheckCache(Now.AddMinutes(minutesAhead), "v1.57.1");
        var expected = minutesAhead <= 5;
        Assert.Equal(expected, UpdateCheckCacheStore.IsFresh(cache, Now, UpdateCheckCacheStore.DefaultInterval));
    }

    [Fact]
    public void Clear_removes_the_record_so_the_next_check_actually_runs()
    {
        var path = TempPath();
        try
        {
            var store = new UpdateCheckCacheStore(path);
            store.Save(new UpdateCheckCache(Now, "v1.57.1"));
            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            CleanUp(path);
        }
    }
}
