using System.Diagnostics;
using System.Text;

namespace EasyPub.Core;

/// <summary>Separate engine adapter: never run KindleGen's binary postprocessor on Kindling output.</summary>
internal static class KindlingWriter
{
    public static string ResolvePath(MobiOptions options) => string.IsNullOrWhiteSpace(options.KindlingPath)
        ? Path.Combine(AppContext.BaseDirectory, "bin", "kindling-cli-windows.exe")
        : Path.GetFullPath(options.KindlingPath);

    public static async Task<(int ChapterCount, long OutputBytes)> WriteAsync(
        ConversionRequest request, CancellationToken token, IProgress<ConversionProgress>? progress)
    {
        var options = request.Options ?? ConversionOptions.LegacyDefault;
        var engine = ResolvePath(options.Mobi);
        if (!File.Exists(engine)) throw new FileNotFoundException("找不到 Kindling，请在制作设置中指定程序路径。", engine);
        var output = Path.GetFullPath(request.OutputPath);
        if (string.Equals(output, Path.GetFullPath(request.InputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("输出不能覆盖原稿。");
        var folder = Path.Combine(Path.GetDirectoryName(output)!, ".easypub-kindling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // Keep the log outside temporary work, including on failure; never overwrite a prior log.
        var log = output + ".kindling-" + Guid.NewGuid().ToString("N") + ".log";
        try
        {
            var epub = Path.Combine(folder, "input.epub");
            int count;
            if (Path.GetExtension(request.InputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                var result = await LegacyEpubWriter.WriteAsync(request with { OutputPath = epub }, token,
                    progress is null ? null : new Progress<ConversionProgress>(p => progress.Report(p with { Fraction = p.Fraction * .65 })));
                count = result.ChapterCount;
            }
            else
            {
                var inspection = EpubInspectionService.Inspect(request.InputPath);
                if (inspection.HasUnsupportedEncryption) throw new InvalidDataException("该 EPUB 含不支持的加密资源。");
                count = inspection.SpineDocumentCount;
                File.Copy(request.InputPath, epub);
            }
            var raw = Path.Combine(folder, "output.mobi");
            var start = new ProcessStartInfo(engine) { WorkingDirectory = folder, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "build", epub, "-o", raw, "--legacy-mobi" }) start.ArgumentList.Add(arg);
            // Desktop conversion accepts books without a cover. KDP publication validation
            // rejects those; keep EasyPub preflight and Kindling's HTML self-check instead.
            start.ArgumentList.Add("--no-validate");
            if (options.Mobi.StripSourceArchive) start.ArgumentList.Add("--no-embed-source");
            progress?.Report(new(request.InputPath, .70, "正在使用 Kindling 转换 MOBI"));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Kindling。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
            finally
            {
                await File.WriteAllTextAsync(log, $"Engine: {engine}\nFormat: MOBI7+KF8\nKDP validation: off (local conversion); HTML self-check: on\n" + await stdout + "\n" + await stderr, CancellationToken.None);
            }
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(raw) || new FileInfo(raw).Length < 100)
                throw new InvalidOperationException($"Kindling 转换失败（退出码 {process.ExitCode}），日志：{log}");
            var header = new byte[68];
            using (var stream = File.OpenRead(raw)) stream.ReadExactly(header);
            if (Encoding.ASCII.GetString(header, 60, 8) != "BOOKMOBI")
                throw new InvalidDataException($"Kindling 未生成有效 MOBI 文件，日志：{log}");
            File.Move(raw, output, overwrite: true);
            progress?.Report(new(request.InputPath, 1, "Kindling 转换完成"));
            return (count, new FileInfo(output).Length);
        }
        finally
        {
            // Only our unique temporary directory; user originals are never targets.
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
