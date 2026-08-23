using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

internal static class LegacyMobiWriter
{
    public static async Task<(int ChapterCount, long OutputBytes)> WriteAsync(
        ConversionRequest request,
        CancellationToken cancellationToken,
        IProgress<ConversionProgress>? progress = null)
    {
        var outputPath = Path.GetFullPath(request.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);

        var options = request.Options ?? ConversionOptions.LegacyDefault;
        var kindleGenPath = KindleGenLocator.Resolve(options.Mobi.KindleGenPath);
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var workingStem = $"{stem}-{Guid.NewGuid():N}";
        var epubPath = Path.Combine(outputDirectory, workingStem + ".epub");
        var workingDirectory = Path.Combine(outputDirectory, ".easypub-modern-" + Guid.NewGuid().ToString("N"));
        var oebpsDirectory = Path.Combine(workingDirectory, "OEBPS");
        var rawMobiName = workingStem + "_out.mobi";
        var rawMobiPath = Path.Combine(oebpsDirectory, rawMobiName);

        try
        {
            var epubResult = await LegacyEpubWriter.WriteAsync(
                request with { OutputPath = epubPath },
                cancellationToken,
                progress is null ? null : new Progress<ConversionProgress>(value =>
                    progress.Report(value with { Fraction = value.Fraction * 0.70 })));
            progress?.Report(new ConversionProgress(request.InputPath, 0.72, "正在准备 KindleGen 转换包"));
            ZipFile.ExtractToDirectory(epubPath, workingDirectory);
            PrepareLegacyMobiPackage(oebpsDirectory, options);

            var startInfo = new ProcessStartInfo(kindleGenPath)
            {
                WorkingDirectory = oebpsDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("content.opf");
            startInfo.ArgumentList.Add($"-c{(int)options.Mobi.Compression}");
            foreach (var argument in CommandLineArguments.Parse(options.Mobi.ExtraArguments))
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(rawMobiName);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 KindleGen。");
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            });
            progress?.Report(new ConversionProgress(request.InputPath, 0.78, "KindleGen 正在生成 MOBI"));
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var log = (await standardOutput) + (await standardError);

            if (!File.Exists(rawMobiPath) || new FileInfo(rawMobiPath).Length == 0)
                throw new InvalidOperationException($"KindleGen 未生成 MOBI（退出码 {process.ExitCode}）。\n{log.Trim()}");

            var mobiBytes = await File.ReadAllBytesAsync(rawMobiPath, cancellationToken);
            progress?.Report(new ConversionProgress(request.InputPath, 0.91, "正在修复并验证 Kindle 联合结构"));
            var kindleGenProducedJointMobi = LegacyMobiPostProcessor.HasValidJointStructure(mobiBytes);
            if (options.Mobi.StripSourceArchive)
                mobiBytes = LegacyMobiPostProcessor.StripSourceArchive(mobiBytes);
            var asin = options.Mobi.EnableReadingProgressSync ? NormalizeOrGenerateAsin(options.Mobi.Asin) : null;
            mobiBytes = LegacyMobiPostProcessor.ApplyEasyPubMetadata(mobiBytes, asin);
            if (kindleGenProducedJointMobi && !LegacyMobiPostProcessor.HasValidJointStructure(mobiBytes))
                throw new InvalidDataException("MOBI 后处理破坏了 Kindle KF8 联合结构，已停止输出无效文件。");
            await File.WriteAllBytesAsync(outputPath, mobiBytes, cancellationToken);
            progress?.Report(new ConversionProgress(request.InputPath, 1, "转换完成"));
            return (epubResult.ChapterCount, mobiBytes.LongLength);
        }
        finally
        {
            TryDelete(epubPath);
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string NormalizeOrGenerateAsin(string? configuredAsin)
    {
        var normalized = configuredAsin?.Trim().ToUpperInvariant();
        if (normalized is not null && Regex.IsMatch(normalized, @"^(B00[A-Z0-9]{7}|\d{10})$", RegexOptions.CultureInvariant))
            return normalized;

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var suffix = new char[7];
        for (var index = 0; index < suffix.Length; index++)
            suffix[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return "B00" + new string(suffix);
    }

    private static void PrepareLegacyMobiPackage(string oebpsDirectory, ConversionOptions options)
    {
        var opfPath = Path.Combine(oebpsDirectory, "content.opf");
        var opf = File.ReadAllText(opfPath, Encoding.UTF8)
            .Replace(
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">",
                "<metadata>\r\n<dc-metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">",
                StringComparison.Ordinal)
            .Replace("</metadata>\r\n<manifest>", "</dc-metadata>\r\n</metadata>\r\n<manifest>", StringComparison.Ordinal);
        File.WriteAllText(opfPath, opf, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var ncxPath = Path.Combine(oebpsDirectory, "toc.ncx");
        var ncx = File.ReadAllText(ncxPath, Encoding.UTF8);
        var chapterOrder = 0;
        ncx = Regex.Replace(
            ncx,
            "(<navPoint id=\"chapter\\d+\" playOrder=\")\\d+(\">)",
            match => match.Groups[1].Value + (++chapterOrder) + match.Groups[2].Value,
            RegexOptions.CultureInvariant);
        File.WriteAllText(ncxPath, ncx, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        foreach (var chapterPath in Directory.EnumerateFiles(oebpsDirectory, "chapter*.html"))
        {
            var chapter = File.ReadAllText(chapterPath, Encoding.UTF8)
                .Replace(
                    "<body>\r\n<h2",
                    "<body>\r\n<a id=\"section\" /><a ></a> <a id=\"article\" /><a ></a>\r\n<h2",
                    StringComparison.Ordinal);
            File.WriteAllText(chapterPath, chapter, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        File.WriteAllText(
            Path.Combine(oebpsDirectory, "style.css"),
            LegacyTemplates.CreateMobiStyleCss(options),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class KindleGenLocator
{
    public static string Resolve(string? configuredPath)
    {
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, "tools", "kindlegen_v2.9.exe"),
            Path.Combine(AppContext.BaseDirectory, "bin", "kindlegen_v2.9.exe"),
            @"C:\Users\13168\Desktop\easypub\bin\kindlegen_v2.9.exe",
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException(
            "未找到 kindlegen_v2.9.exe。请在 MOBI 选项中选择原版 EasyPub 的 KindleGen 文件。",
            configuredPath);
    }
}

internal static class CommandLineArguments
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quoted) throw new ArgumentException("KindleGen 额外参数中的引号没有闭合。", nameof(value));
        if (current.Length > 0) arguments.Add(current.ToString());
        return arguments;
    }
}
