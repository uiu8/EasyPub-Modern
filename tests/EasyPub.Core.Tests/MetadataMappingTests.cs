using System.IO.Compression;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class MetadataMappingTests
{
    [Fact]
    public void Most_specific_folder_rule_matches_imported_book()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-map-{Guid.NewGuid():N}");
        var child = Path.Combine(root, "起点", "玄幻");
        var input = Path.Combine(child, "小说.txt");
        var rules = new[]
        {
            new FolderMetadataRule(root, new BookMetadataOverrides { Publisher = "通用" }),
            new FolderMetadataRule(child, new BookMetadataOverrides { Publisher = "起点" }),
        };

        var matched = MetadataMappingResolver.Match(input, rules);

        Assert.NotNull(matched);
        Assert.Equal("起点", matched.Metadata.Publisher);
    }

    [Fact]
    public void Similar_folder_prefix_does_not_match()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-map-{Guid.NewGuid():N}");
        var ruleFolder = Path.Combine(root, "起点");
        var siblingInput = Path.Combine(root, "起点备份", "小说.txt");

        var matched = MetadataMappingResolver.Match(siblingInput,
            [new FolderMetadataRule(ruleFolder, new BookMetadataOverrides { Publisher = "起点" })]);

        Assert.Null(matched);
    }

    [Fact]
    public async Task Mapping_store_round_trip_normalizes_and_replaces_duplicate_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-map-store-{Guid.NewGuid():N}");
        var storagePath = Path.Combine(root, "metadata-mappings.json");
        var mappedFolder = Path.Combine(root, "books");
        try
        {
            await new MetadataMappingStore(storagePath).SaveAsync([
                new FolderMetadataRule(mappedFolder, new BookMetadataOverrides { Publisher = "旧值" }),
                new FolderMetadataRule(mappedFolder + Path.DirectorySeparatorChar, new BookMetadataOverrides { Publisher = "起点" }),
            ]);

            var restored = await new MetadataMappingStore(storagePath).LoadAsync();

            var rule = Assert.Single(restored);
            Assert.Equal(MetadataMappingResolver.NormalizeFolder(mappedFolder), rule.FolderPath);
            Assert.Equal("起点", rule.Metadata.Publisher);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Batch_request_uses_folder_metadata_and_keeps_manual_author_priority()
    {
        var source = new BookConversionSource(
            @"C:\books\起点\小说.txt",
            Author: "手动作者",
            MetadataOverrides: new BookMetadataOverrides
            {
                Author = "映射作者",
                Publisher = "起点",
                Category = "网络文学",
            });
        var baseOptions = ConversionOptions.LegacyDefault with
        {
            Metadata = new PublicationMetadata { Publisher = "统一出版社", Language = "zh-CN" },
        };

        var request = Assert.Single(BatchConversionRequestFactory.Create(
            [source], Path.GetTempPath(), "epub", "统一作者", baseOptions));

        Assert.Equal("手动作者", request.Author);
        Assert.Equal("起点", request.Options!.Metadata.Publisher);
        Assert.Equal("网络文学", request.Options.Metadata.Category);
        Assert.Equal("zh-CN", request.Options.Metadata.Language);
    }

    [Fact]
    public async Task Folder_publisher_mapping_is_written_to_epub_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-map-epub-{Guid.NewGuid():N}");
        var inputFolder = Path.Combine(root, "起点");
        var outputFolder = Path.Combine(root, "output");
        var input = Path.Combine(inputFolder, "雨夜.txt");
        Directory.CreateDirectory(inputFolder);
        await File.WriteAllTextAsync(input, "第一章 雨夜\n正文", Encoding.UTF8);
        try
        {
            var rule = new FolderMetadataRule(inputFolder, new BookMetadataOverrides { Publisher = "起点" });
            var matched = MetadataMappingResolver.Match(input, [rule]);
            var request = Assert.Single(BatchConversionRequestFactory.Create(
                [new BookConversionSource(input, MetadataOverrides: matched!.Metadata)],
                outputFolder,
                "epub",
                null,
                ConversionOptions.LegacyDefault));

            await new EasyPubConverter().ConvertAsync(request);

            using var archive = ZipFile.OpenRead(request.OutputPath);
            var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == "OEBPS/content.opf");
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var opf = await reader.ReadToEndAsync();
            Assert.Contains("<dc:publisher>起点</dc:publisher>", opf);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
