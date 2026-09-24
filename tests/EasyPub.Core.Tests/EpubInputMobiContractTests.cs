using System.Security.Cryptography;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// EPUB 输入到 MOBI 有两条不同的边界：保留原 EPUB 版式，或导入后按
/// EasyPub 兼容规则重排。两条路径都必须忽略 TXT 章节树和全局 TXT 识别规则，
/// 且转换前检查报出的数量要与实际转换回执一致。
/// </summary>
public sealed class EpubInputMobiContractTests
{
    [Theory]
    [InlineData(EpubInputMode.PreserveOriginal)]
    [InlineData(EpubInputMode.EasyPubCompatible)]
    public async Task Epub_input_mode_has_one_count_boundary_and_does_not_use_txt_tree(EpubInputMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-epub-input-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var text = Path.Combine(root, "source.txt");
        var epub = Path.Combine(root, "source.epub");
        var mobi = Path.Combine(root, "result.mobi");
        var kindlegen = Path.Combine(FindWorkspaceRoot(), "work", "easypub-compat", "legacy-capture", "bin", "kindlegen_v2.9.exe");
        await File.WriteAllTextAsync(text,
            "第一章 原题\n正文一\n第二章 原题\n正文二\n第三章 原题\n正文三\n");

        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                text,
                epub,
                Options: new ConversionOptions
                {
                    TocHierarchy = new TocHierarchyOptions { IncludeHtmlTocPage = true },
                }));

            // This plan is deliberately for the TXT source. If either EPUB input path
            // accidentally feeds it into LegacyTextParser, the source hash check fails.
            var txtTree = await ChapterTreeDocument.LoadAsync(
                text,
                "^第[一二三]章.*$",
                new TocHierarchyOptions());
            var originalEpubHash = Hash(epub);
            var options = ConversionOptions.LegacyDefault with
            {
                ChapterPattern = "^这条规则不应读取$",
                TocHierarchy = new TocHierarchyOptions { Enabled = false },
                Mobi = new MobiOptions
                {
                    KindleGenPath = kindlegen,
                    EpubInputMode = mode,
                },
            };
            var request = new ConversionRequest(epub, mobi, Options: options)
            {
                ChapterTree = txtTree.CreatePlan(txtTree.Entries),
            };

            var report = await new ConversionPreflightInspector().InspectAsync([request]);
            var book = Assert.Single(report.Books);
            Assert.DoesNotContain(report.Issues, issue => issue.Code == "epub_output_unsupported");

            var rawSpineCount = EpubInspectionService.Inspect(epub).SpineDocumentCount;
            if (mode == EpubInputMode.PreserveOriginal)
            {
                Assert.Equal(rawSpineCount, book.ChapterCandidateCount);
            }
            else
            {
                // Compatible reflow drops cover/navigation spine items. The preflight
                // count must therefore be the logical reflow count, not the raw spine.
                Assert.True(book.ChapterCandidateCount < rawSpineCount);
            }

            var result = await new EasyPubConverter().ConvertAsync(request);
            Assert.Equal(book.ChapterCandidateCount, result.ChapterCount);
            Assert.Equal(originalEpubHash, Hash(epub));
            Assert.True(new FileInfo(mobi).Length > 1024);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EasyPub.Modern.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("找不到 EasyPub 工作区。");
    }
}
