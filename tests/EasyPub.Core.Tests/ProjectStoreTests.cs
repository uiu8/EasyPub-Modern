using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ProjectStoreTests
{
    [Fact]
    public async Task Project_round_trip_preserves_per_book_assets_and_profile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "测试.easypubproj");
        var document = new EasyPubProjectDocument(
            EasyPubProjectDocument.CurrentSchemaVersion,
            path,
            directory,
            ConversionProfile.Default with
            {
                OutputFormat = "mobi",
                Options = ConversionOptions.LegacyDefault with
                {
                    TocHierarchy = new TocHierarchyOptions { Enabled = true },
                    ArtifactValidation = new ArtifactValidationOptions { Enabled = true, MaxReportCount = 50 },
                    Font = new EmbeddedFontOptions
                    {
                        Enabled = true,
                        FontPath = Path.Combine(directory, "font.ttf"),
                        FamilyName = "测试字体",
                    },
                    Metadata = new PublicationMetadata
                    {
                        Translator = "译者",
                        Isbn = "978-7-121-15535-2",
                        PublicationDate = new DateOnly(2026, 8, 23),
                        Publisher = "出版社",
                        Category = "小说",
                        Language = "zh-CN",
                        Description = "简介",
                        CustomMetadata = [new CalibreCustomMetadata
                        {
                            LookupName = "kindlecollections",
                            ColumnHeading = "Kindle书架",
                            Type = CalibreCustomMetadataType.TextList,
                            Value = "起点, 完结",
                        }],
                    },
                },
            },
            [new EasyPubProjectBook(
                Path.Combine(directory, "小说.txt"),
                "书名",
                "作者",
                Path.Combine(directory, "cover.webp"),
                [new BookIllustration("雨夜", Path.Combine(directory, "rain.png"), "雨夜图", 12)])
            {
                MetadataOverrides = new BookMetadataOverrides { Publisher = "起点", Category = "网络文学" },
                MetadataRuleFolder = Path.Combine(directory, "起点"),
                ChapterTree = new ChapterTreePlan("ABC123", [
                    new ChapterTreeEntry("chapter-1", "第一章", 1, true, 1, [new ChapterSourceRange(2, 3)])
                    { HeadingLevel = 2 }]),
            }],
            DateTimeOffset.Now);

        try
        {
            var store = new EasyPubProjectStore(path);
            await store.SaveAsync(document);
            var loaded = await store.LoadAsync();

            Assert.Equal("mobi", loaded.Profile.OutputFormat);
            Assert.True(loaded.Profile.Options.Font.Enabled);
            Assert.True(loaded.Profile.Options.TocHierarchy.Enabled);
            Assert.True(loaded.Profile.Options.ArtifactValidation.Enabled);
            Assert.Equal(50, loaded.Profile.Options.ArtifactValidation.MaxReportCount);
            Assert.Equal("译者", loaded.Profile.Options.Metadata.Translator);
            Assert.Equal(new DateOnly(2026, 8, 23), loaded.Profile.Options.Metadata.PublicationDate);
            Assert.Equal("#kindlecollections", Assert.Single(loaded.Profile.Options.Metadata.CustomMetadata).CalibreLookupName);
            var book = Assert.Single(loaded.Books);
            Assert.Equal("书名", book.Title);
            Assert.Equal(12, Assert.Single(book.Illustrations).InsertAfterLine);
            Assert.Equal("起点", book.MetadataOverrides.Publisher);
            Assert.Equal(Path.Combine(directory, "起点"), book.MetadataRuleFolder);
            Assert.Equal("第一章", Assert.Single(book.ChapterTree!.Entries).Title);
            Assert.Equal(EasyPubProjectStore.Fingerprint(document), EasyPubProjectStore.Fingerprint(loaded));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
