using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class EpubToMobiTests
{
    [Theory]
    [InlineData(EpubInputMode.PreserveOriginal)]
    [InlineData(EpubInputMode.EasyPubCompatible)]
    public async Task Epub_is_converted_to_a_valid_joint_mobi(EpubInputMode mode)
    {
        var root = FindWorkspaceRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-epub-mobi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var epub = Path.Combine(directory, "source.epub");
        var mobi = Path.Combine(directory, "result.mobi");
        var kindleGen = Path.Combine(root, "work", "easypub-compat", "legacy-capture", "bin", "kindlegen_v2.9.exe");
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(input, epub, "EPUB 输入测试", "Codex"));
            var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(epub)));
            var options = ConversionOptions.LegacyDefault with
            {
                Mobi = new MobiOptions { KindleGenPath = kindleGen, EpubInputMode = mode },
            };
            var result = await new EasyPubConverter().ConvertAsync(new ConversionRequest(epub, mobi, Options: options));
            var bytes = await File.ReadAllBytesAsync(mobi);

            Assert.True(result.ChapterCount >= 4);
            Assert.Equal("BOOKMOBI", System.Text.Encoding.ASCII.GetString(bytes, 60, 8));
            Assert.True(bytes.Length > 1024);
            Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(epub))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Epub_input_cannot_be_written_as_epub()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-input-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => new EasyPubConverter().ConvertAsync(
                new ConversionRequest(path, Path.ChangeExtension(path, ".copy.epub"))));
        }
        finally
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".copy.epub"));
        }
    }

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
