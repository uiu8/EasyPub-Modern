using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class CalibreCustomMetadataTests
{
    [Fact]
    public void Lookup_name_is_normalized_and_list_values_are_cleaned()
    {
        var item = Assert.Single(CalibreCustomMetadata.NormalizeAll([
            new CalibreCustomMetadata
            {
                LookupName = "KindleCollections",
                ColumnHeading = " Kindle书架 ",
                Type = CalibreCustomMetadataType.TextList,
                Value = "起点， 完结,起点",
            },
        ]));

        Assert.Equal("#kindlecollections", item.CalibreLookupName);
        Assert.Equal("Kindle书架", item.DisplayHeading);
        Assert.Equal("起点, 完结", item.Value);
    }

    [Theory]
    [InlineData("Kindle 书架")]
    [InlineData("#kindle:collections")]
    [InlineData("123column")]
    public void Invalid_lookup_name_is_rejected(string lookupName)
    {
        Assert.Throws<ArgumentException>(() => CalibreCustomMetadata.NormalizeAll([
            new CalibreCustomMetadata { LookupName = lookupName, Value = "起点" },
        ]));
    }

    [Fact]
    public void Empty_value_preserves_the_field_definition()
    {
        var item = Assert.Single(CalibreCustomMetadata.NormalizeAll([
            new CalibreCustomMetadata
            {
                LookupName = "kindlecollections",
                ColumnHeading = "Kindle书架",
                Type = CalibreCustomMetadataType.TextList,
                Value = "，,，",
            },
        ]));

        Assert.Equal("#kindlecollections", item.CalibreLookupName);
        Assert.Equal("Kindle书架", item.DisplayHeading);
        Assert.False(item.HasValue);
    }

    [Fact]
    public void Existing_field_definitions_are_reused_without_copying_unified_values()
    {
        var assignments = CalibreCustomMetadata.PrepareAssignments([
            new CalibreCustomMetadata
            {
                LookupName = "kindlecollections",
                ColumnHeading = "Kindle书架",
                Type = CalibreCustomMetadataType.TextList,
                Value = "统一值",
            },
        ], [new CalibreCustomMetadata
        {
            LookupName = "kindlecollections",
            ColumnHeading = "旧标题",
            Type = CalibreCustomMetadataType.Text,
            Value = "逐书值",
        }]);

        var item = Assert.Single(assignments);
        Assert.Equal("#kindlecollections", item.CalibreLookupName);
        Assert.Equal("Kindle书架", item.DisplayHeading);
        Assert.Equal(CalibreCustomMetadataType.TextList, item.Type);
        Assert.Equal("逐书值", item.Value);
    }

    [Fact]
    public void Folder_value_overrides_unified_value_by_lookup_name()
    {
        var unified = new PublicationMetadata
        {
            CustomMetadata = [new CalibreCustomMetadata { LookupName = "source", Value = "统一" }],
        };
        var mapped = new BookMetadataOverrides
        {
            CustomMetadata = [new CalibreCustomMetadata { LookupName = "#SOURCE", Value = "起点" }],
        };

        var result = MetadataMappingResolver.Apply(unified, mapped);

        var custom = Assert.Single(result.CustomMetadata);
        Assert.Equal("#source", custom.CalibreLookupName);
        Assert.Equal("起点", custom.Value);
    }

    [Fact]
    public void Blank_scoped_value_keeps_the_unified_fallback()
    {
        var unified = new PublicationMetadata
        {
            CustomMetadata = [new CalibreCustomMetadata { LookupName = "source", Value = "统一" }],
        };
        var scoped = new BookMetadataOverrides
        {
            CustomMetadata = [new CalibreCustomMetadata { LookupName = "source", Value = "" }],
        };

        var custom = Assert.Single(MetadataMappingResolver.Apply(unified, scoped).CustomMetadata);

        Assert.Equal("统一", custom.Value);
    }

    [Fact]
    public async Task Epub_embeds_safe_calibre_custom_column_metadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-calibre-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var output = Path.Combine(directory, "小说.epub");
        await File.WriteAllTextAsync(input, "第一章 雨夜\n正文", Encoding.UTF8);
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                "测试书",
                "测试作者",
                ConversionOptions.LegacyDefault with
                {
                    Metadata = new PublicationMetadata
                    {
                        CustomMetadata = [new CalibreCustomMetadata
                        {
                            LookupName = "kindlecollections",
                            ColumnHeading = "Kindle书架",
                            Type = CalibreCustomMetadataType.TextList,
                            Value = "起点, 完结",
                        }],
                    },
                }));

            using var archive = ZipFile.OpenRead(output);
            var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == "OEBPS/content.opf");
            await using var stream = entry.Open();
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
            var meta = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "meta" &&
                (string?)element.Attribute("name") == "calibre:user_metadata:#kindlecollections");
            var content = Assert.IsType<string>((string?)meta.Attribute("content"));
            using var json = JsonDocument.Parse(content);
            Assert.Equal("text", json.RootElement.GetProperty("datatype").GetString());
            Assert.Equal("kindlecollections", json.RootElement.GetProperty("label").GetString());
            Assert.Equal("Kindle书架", json.RootElement.GetProperty("name").GetString());
            Assert.Equal("|", json.RootElement.GetProperty("is_multiple").GetString());
            Assert.Equal(["起点", "完结"], json.RootElement.GetProperty("#value#").EnumerateArray().Select(value => value.GetString()));
            Assert.False(content.Contains("composite", StringComparison.OrdinalIgnoreCase));
            Assert.False(content.Contains("template", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Epub_skips_defined_custom_columns_that_have_no_value()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-calibre-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var output = Path.Combine(directory, "小说.epub");
        await File.WriteAllTextAsync(input, "第一章 雨夜\n正文", Encoding.UTF8);
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                "测试书",
                "测试作者",
                ConversionOptions.LegacyDefault with
                {
                    Metadata = new PublicationMetadata
                    {
                        CustomMetadata = [new CalibreCustomMetadata
                        {
                            LookupName = "kindlecollections",
                            ColumnHeading = "Kindle书架",
                            Type = CalibreCustomMetadataType.TextList,
                        }],
                    },
                }));

            using var archive = ZipFile.OpenRead(output);
            var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == "OEBPS/content.opf");
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var opf = await reader.ReadToEndAsync();
            Assert.DoesNotContain("calibre:user_metadata:#kindlecollections", opf, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
