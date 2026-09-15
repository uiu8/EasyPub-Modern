using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// The source list is user data: it is reordered, extended and disabled from the UI, and the code must
/// still be able to ship a new built-in source or fix a broken search address without losing any of it.
/// These lock the merge rules and the two site-specific parsers.
/// </summary>
public class BookSourceTests
{
    private const string FanqieHtml = """
        <html><head><title>十日终焉完整版在线免费阅读_十日终焉小说_番茄小说官网</title></head><body>
        <div class="page-directory-header"><h3><span>目录</span><span class="directory-dot"></span>1496<!-- -->章</h3></div>
        <div class="page-directory-content">
        <div><div class="volume volume_first">第一卷：我听到了你们<span class="volume-dot"></span>共<!-- -->2<!-- -->章</div>
        <div class="chapter">
        <div class="chapter-item"><a href="/reader/7173216089122439711" class="chapter-item-title" target="_blank">第1章 空屋</a></div>
        <div class="chapter-item"><a href="/reader/7173217408294453791" class="chapter-item-title" target="_blank">第2章 说谎</a></div>
        </div>
        <div><div class="volume">第二卷：我看到了你们<span class="volume-dot"></span>共<!-- -->2<!-- -->章</div>
        <div class="chapter">
        <div class="chapter-item"><a href="/reader/7173615024101917184" class="chapter-item-title" target="_blank">第1章 重逢</a></div>
        <div class="chapter-item"><a href="/reader/7174036649100182048" class="chapter-item-title" target="_blank">第2章 交易</a></div>
        </div></div>
        </div></body></html>
        """;

    [Fact]
    public void Fanqie_directory_keeps_each_chapter_under_its_own_volume()
    {
        var catalog = ReferenceCatalogClient.Parse(
            new Uri("https://fanqienovel.com/page/7143038691944959011"), FanqieHtml);

        Assert.Equal(
            new[] { "第一卷：我听到了你们", "第二卷：我看到了你们" },
            catalog.VolumeTitles.ToArray());
        Assert.Equal(
            new[] { "第1章 空屋", "第2章 说谎", "第1章 重逢", "第2章 交易" },
            catalog.Titles.ToArray());
        Assert.Equal("第一卷：我听到了你们", catalog.Nodes[1].VolumeTitle);
        Assert.Equal("第二卷：我看到了你们", catalog.Nodes[^1].VolumeTitle);
    }

    /// <summary>
    /// The volume banner reads "第一卷：我听到了你们共2章" once the nested tags are stripped, so the
    /// count has to be cut away or every volume name would carry a stale chapter total.
    /// </summary>
    [Fact]
    public void Fanqie_volume_name_drops_the_chapter_count_suffix()
    {
        var catalog = ReferenceCatalogClient.Parse(
            new Uri("https://fanqienovel.com/page/7143038691944959011"), FanqieHtml);

        Assert.DoesNotContain("共", catalog.VolumeTitles[0]);
        Assert.DoesNotContain("章", catalog.VolumeTitles[0]);
    }

    [Fact]
    public void Built_in_order_puts_qidian_and_fanqie_first()
    {
        var sources = BookSourceCatalog.Merge(null);

        Assert.Equal(new[] { "qidian", "fanqie" }, sources.Take(2).Select(source => source.Id).ToArray());
        Assert.All(sources, source => Assert.True(source.Enabled));
        Assert.All(sources, source => Assert.True(source.BuiltIn));
    }

    [Fact]
    public void Fanqie_is_direct_only_while_the_others_search_by_name()
    {
        var fanqie = BookSourceCatalog.Find("fanqie")!;
        var qidian = BookSourceCatalog.Find("qidian")!;

        Assert.False(fanqie.Searchable);
        Assert.True(fanqie.Direct);
        Assert.Equal("仅网址/编号", fanqie.Capability);
        Assert.True(qidian.Searchable);
        Assert.True(qidian.Direct);
    }

    [Fact]
    public void Merge_keeps_user_settings_and_restores_a_missing_built_in()
    {
        var saved = new[]
        {
            new BookSource { Id = "owlook", Name = "偶书网", SearchUrl = "https://stale", Priority = 5, Enabled = false },
            new BookSource { Id = "custom:example.com", Name = "少年梦", SearchUrl = "https://www.example.com/s?q={q}", Priority = 15 },
        };

        var merged = BookSourceCatalog.Merge(saved);

        Assert.Equal(7, merged.Count);                                  // six built-ins plus the user's own
        Assert.Equal("owlook", merged[0].Id);
        Assert.False(merged[0].Enabled);                                 // the user's choice survives
        Assert.Equal("https://www.owlook.com.cn/search?wd={q}", merged[0].SearchUrl);   // the code owns the address
        Assert.Contains(merged, source => source.Id == "fanqie");        // a new built-in is adopted
        Assert.Contains(merged, source => source.Id == "custom:example.com");
    }

    [Fact]
    public void Renumber_leaves_even_gaps_for_the_move_buttons()
    {
        var renumbered = BookSourceCatalog.Renumber(BookSourceCatalog.Merge(null));

        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60 }, renumbered.Select(source => source.Priority).ToArray());
    }

    [Fact]
    public void Arrange_puts_the_folder_rules_sources_ahead_of_the_global_order()
    {
        var sources = BookSourceCatalog.Merge(null);

        var arranged = ReferenceCatalogClient.Arrange(sources, ["owlook", "fanqie"]).ToArray();

        Assert.Equal(new[] { "owlook", "fanqie", "qidian" }, arranged.Take(3).Select(source => source.Id).ToArray());
        Assert.Equal(6, arranged.Length);
    }

    [Fact]
    public void Arrange_without_a_preference_uses_the_configured_priority()
    {
        var arranged = ReferenceCatalogClient.Arrange(BookSourceCatalog.Merge(null), null).ToArray();

        Assert.Equal("qidian", arranged[0].Id);
        Assert.Equal("fanqie", arranged[1].Id);
    }

    [Theory]
    [InlineData("7143038691944959011", true)]
    [InlineData("1041162879", true)]
    [InlineData("十日终焉", false)]
    [InlineData("1234", false)]
    public void LooksLikeIdentifier_only_accepts_a_bare_id(string value, bool expected)
    {
        Assert.Equal(expected, ReferenceCatalogClient.LooksLikeIdentifier(value));
    }

    [Fact]
    public void Search_address_template_is_filled_and_validated()
    {
        var uri = ReferenceCatalogClient.BuildSearchUri(BookSourceCatalog.Find("shudugu")!, "十日终焉");

        Assert.Equal("https://www.shudugu.org/i/sor.aspx?key=%E5%8D%81%E6%97%A5%E7%BB%88%E7%84%89", uri.AbsoluteUri);
    }

    [Fact]
    public void A_source_without_a_search_address_is_refused_rather_than_guessed()
    {
        var fanqie = BookSourceCatalog.Find("fanqie")!;

        var error = Assert.Throws<InvalidOperationException>(() => ReferenceCatalogClient.BuildSearchUri(fanqie, "十日终焉"));

        Assert.Contains("不支持按书名搜索", error.Message);
    }

    [Fact]
    public void Folder_rule_reports_its_preferred_sources_by_display_name()
    {
        var rule = new FolderMetadataRule(
            @"C:\novels\tomato",
            new BookMetadataOverrides(),
            ["fanqie", "qidian"]);

        Assert.Equal(new[] { "fanqie", "qidian" }, rule.Sources.ToArray());
        Assert.Equal("番茄小说 / 起点中文网", rule.SourceLabel);
    }

    [Fact]
    public void Folder_rule_without_a_preference_falls_back_to_the_global_order()
    {
        var rule = new FolderMetadataRule(@"C:\novels", new BookMetadataOverrides());

        Assert.Empty(rule.Sources);
        Assert.Equal("", rule.SourceLabel);
    }

    [Fact]
    public async Task Saved_sources_survive_a_round_trip_and_adopt_new_built_ins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-sources-{Guid.NewGuid():N}.json");
        try
        {
            var store = new BookSourceStore(path);
            await store.SaveAsync(
            [
                new BookSource { Id = "custom:example.com", Name = "少年梦", SearchUrl = "https://www.example.com/s?q={q}", Priority = 5 },
                new BookSource { Id = "qidian", Name = "起点中文网", SearchUrl = "https://www.qidian.com/so/{q}.html", Cookie = "w_tsfp=x", Priority = 20 },
            ]);

            var loaded = await store.LoadAsync();

            var custom = loaded.Single(source => source.Id == "custom:example.com");
            Assert.Equal("少年梦", custom.Name);
            Assert.Equal(5, custom.Priority);
            Assert.Equal("w_tsfp=x", loaded.Single(source => source.Id == "qidian").Cookie);
            Assert.Equal(7, loaded.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task A_corrupt_source_file_falls_back_to_the_built_in_list()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-sources-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ this is not json");

            var loaded = await new BookSourceStore(path).LoadAsync();

            Assert.Equal(6, loaded.Count);
            Assert.Equal("qidian", loaded[0].Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Folder_rules_keep_their_preferred_sources_across_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-rules-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MetadataMappingStore(path);
            await store.SaveAsync(
            [
                new FolderMetadataRule(
                    @"C:\novels\tomato",
                    new BookMetadataOverrides { Publisher = "番茄小说" },
                    ["fanqie", "fanqie", "  "]),
            ]);

            var loaded = await store.LoadAsync();

            Assert.Equal(new[] { "fanqie" }, loaded[0].Sources.ToArray());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The metadata preview is where a user checks that a folder rule did what they meant, so the
    /// source preference has to show up there next to the metadata it travels with.
    /// </summary>
    [Fact]
    public void Metadata_preview_names_the_preferred_sources()
    {
        var previews = MetadataMappingResolver.Preview(
            [@"C:\novels\tomato\十日终焉.txt"],
            [new FolderMetadataRule(@"C:\novels\tomato", new BookMetadataOverrides(), ["fanqie"])]);

        Assert.Contains("优先书源=番茄小说", previews[0].AppliedValues);
    }

    /// <summary>
    /// Live check against the real site. Opt in with EASYPUB_LIVE_TESTS=1 so the default suite stays
    /// offline and deterministic; run it by hand after touching a site parser.
    /// </summary>
    [Fact]
    public async Task Live_fanqie_page_yields_volumes_and_chapters()
    {
        if (Environment.GetEnvironmentVariable("EASYPUB_LIVE_TESTS") != "1") return;
        var catalog = await new ReferenceCatalogClient()
            .FetchAsync("https://fanqienovel.com/page/7143038691944959011", CancellationToken.None);
        Assert.True(catalog.VolumeTitles.Count >= 5, $"只解析到 {catalog.VolumeTitles.Count} 卷");
        Assert.True(catalog.Titles.Count >= 1000, $"只解析到 {catalog.Titles.Count} 章");
    }
}
