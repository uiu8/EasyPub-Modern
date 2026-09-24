using System.IO.Compression;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class OutputStructureContractTests
{
    [Fact]
    public async Task Saved_tree_is_the_same_source_for_epub_preview_and_preflight()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-output-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "contract.txt");
        var output = Path.Combine(root, "contract.epub");
        await File.WriteAllTextAsync(input,
            "前置正文\n\n第一章 原题\n正文标记一\n第二章 原题\n正文标记二\n",
            Encoding.UTF8);
        try
        {
            var tree = await ChapterTreeDocument.LoadAsync(
                input,
                chapterPattern: "^第[一二]章.*$",
                hierarchy: new TocHierarchyOptions { IncludeHtmlTocPage = true });
            var entries = tree.Entries.ToArray();
            entries[1] = entries[1] with { Title = "第一章 成品标题" };
            entries[2] = entries[2] with { Title = "第二章 成品标题" };
            var plan = tree.CreatePlan(entries);
            var request = CreateRequest(input, output, plan, ConversionOptions.LegacyDefault with
            {
                ChapterPattern = "^全局规则$",
                TocHierarchy = new TocHierarchyOptions { Enabled = false },
            });

            var result = await new EasyPubConverter().ConvertAsync(request);
            Assert.Equal(plan.Entries.Count, result.ChapterCount);
            using (var archive = ZipFile.OpenRead(output))
            {
                var first = await ReadTextAsync(archive, "OEBPS/chapter1.html");
                var second = await ReadTextAsync(archive, "OEBPS/chapter2.html");
                Assert.Contains("第一章 成品标题", first);
                Assert.Contains("正文标记一", first);
                Assert.Contains("第二章 成品标题", second);
                Assert.Contains("正文标记二", second);
            }

            var report = await new ConversionPreflightInspector().InspectAsync([request]);
            var book = Assert.Single(report.Books);
            Assert.Equal(plan.Entries.Count(entry => entry.TitleLineNumber.HasValue), book.ChapterCandidateCount);
            Assert.DoesNotContain(report.Issues, issue => issue.Code is "chapter_not_found" or "repeated_heading_split");

            using var preview = await new BookPreviewService().BuildAsync(request);
            var previewChapters = preview.Items.Where(item => item.IsChapter).ToArray();
            Assert.Equal(plan.Entries.Count, previewChapters.Length);
            Assert.Contains(previewChapters, item => item.Title == "第一章 成品标题");
            Assert.Contains(previewChapters, item => item.Title == "第二章 成品标题");
            Assert.Contains("正文标记一", await File.ReadAllTextAsync(
                previewChapters.Single(item => item.Title == "第一章 成品标题").HtmlPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_tree_keeps_mobi_logical_count_and_packed_toc_titles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-mobi-tree-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "contract.txt");
        var epub = Path.Combine(root, "contract.epub");
        var mobi = Path.Combine(root, "contract.mobi");
        await File.WriteAllTextAsync(input,
            "前置正文\n第一章 原题\n正文标记一\n第二章 原题\n正文标记二\n第三章 原题\n正文标记三\n",
            Encoding.UTF8);
        try
        {
            var tree = await ChapterTreeDocument.LoadAsync(
                input,
                chapterPattern: "^第[一二三]章.*$",
                hierarchy: new TocHierarchyOptions { IncludeHtmlTocPage = true });
            var entries = tree.Entries.ToArray();
            entries[1] = entries[1] with { Title = "第一章 成品一" };
            entries[2] = entries[2] with { Title = "第二章 成品二" };
            entries[3] = entries[3] with { Title = "第三章 成品三" };
            var plan = tree.CreatePlan(entries);
            var baseOptions = ConversionOptions.LegacyDefault with
            {
                ChapterPattern = "^全局规则$",
                TocHierarchy = new TocHierarchyOptions { Enabled = false },
                Mobi = new MobiOptions
                {
                    KindleGenPath = KindleGenLocator.Resolve(null),
                    OptimizeContentPackaging = true,
                },
            };
            var request = CreateRequest(input, mobi, plan, baseOptions);

            var result = await new EasyPubConverter().ConvertAsync(request);
            Assert.Equal(plan.Entries.Count, result.ChapterCount);
            var mobiBytes = await File.ReadAllBytesAsync(mobi);
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(mobiBytes, 60, 8));
            Assert.True(LegacyMobiPostProcessor.HasValidJointStructure(mobiBytes));

            // The Kindle package is assembled from the same EPUB tree before KindleGen compresses it.
            // Inspecting that intermediate package proves logical titles survive the physical packing step.
            var epubRequest = request with { OutputPath = epub };
            await new EasyPubConverter().ConvertAsync(epubRequest);
            var expanded = Path.Combine(root, "expanded");
            ZipFile.ExtractToDirectory(epub, expanded);
            var oebps = Path.Combine(expanded, "OEBPS");
            var packing = MobiContentPackager.Optimize(oebps);
            Assert.Equal(plan.Entries.Count - 1, packing.LogicalChapterCount);
            var toc = await File.ReadAllTextAsync(Path.Combine(oebps, "book-toc.html"));
            Assert.Contains("第一章 成品一", toc);
            Assert.Contains("第二章 成品二", toc);
            Assert.Contains("第三章 成品三", toc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ConversionRequest CreateRequest(
        string input,
        string output,
        ChapterTreePlan plan,
        ConversionOptions options)
    {
        var request = Assert.Single(BatchConversionRequestFactory.Create(
            [new BookConversionSource(input, ChapterTree: plan)],
            Path.GetDirectoryName(output)!,
            Path.GetExtension(output).TrimStart('.'),
            null,
            options));
        return request with { OutputPath = output };
    }

    private static async Task<string> ReadTextAsync(ZipArchive archive, string path)
    {
        await using var stream = archive.GetEntry(path)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }
}
