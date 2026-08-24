using System.Diagnostics;

namespace EasyPub.Core;

public sealed record KindlePreviewerInstallation(string ExecutablePath);

public sealed record KindlePreviewLaunch(string PreviewFilePath, Process Process);

public sealed class KindlePreviewerLauncher
{
    public const string OfficialDownloadPage = "https://kdp.amazon.com/en_US/help/topic/G202131170";

    public KindlePreviewerInstallation? Discover(
        IEnumerable<string>? additionalSearchRoots = null,
        string? pathEnvironment = null)
    {
        foreach (var directory in (pathEnvironment ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in new[] { "kindlepreviewer.bat", "kindlepreviewer.cmd", "kindlepreviewer.exe" })
            {
                var executable = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(executable)) return new KindlePreviewerInstallation(Path.GetFullPath(executable));
            }
        }

        var roots = (additionalSearchRoots ?? [])
            .Concat(DefaultSearchRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var executable = FindInRoot(root);
            if (executable is not null) return new KindlePreviewerInstallation(executable);
        }
        return null;
    }

    public KindlePreviewLaunch Launch(
        string sourceEpubPath,
        string bookName,
        KindlePreviewerInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var previewFile = PreparePreviewCopy(sourceEpubPath, bookName);
        var startInfo = CreateStartInfo(previewFile, installation);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Kindle Previewer 未能启动。");
        return new KindlePreviewLaunch(previewFile, process);
    }

    public KindlePreviewLaunch LaunchWithOtherViewer(
        string sourceEpubPath,
        string bookName,
        string executablePath)
    {
        if (!File.Exists(executablePath)) throw new FileNotFoundException("找不到所选电子书预览器。", executablePath);
        var previewFile = PreparePreviewCopy(sourceEpubPath, bookName);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
        };
        startInfo.ArgumentList.Add(previewFile);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("所选电子书预览器未能启动。");
        return new KindlePreviewLaunch(previewFile, process);
    }

    public ProcessStartInfo CreateStartInfo(
        string previewFilePath,
        KindlePreviewerInstallation installation)
    {
        if (!File.Exists(previewFilePath)) throw new FileNotFoundException("找不到待预览的 EPUB。", previewFilePath);
        if (!File.Exists(installation.ExecutablePath))
            throw new FileNotFoundException("找不到 Kindle Previewer。", installation.ExecutablePath);
        var executable = Path.GetFullPath(installation.ExecutablePath);
        var executableExtension = Path.GetExtension(executable);
        var isCommandScript = executableExtension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                              || executableExtension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : executable,
            UseShellExecute = false,
            CreateNoWindow = isCommandScript,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"\"{executable}\" \"{Path.GetFullPath(previewFilePath)}\"\"");
        }
        else
        {
            startInfo.ArgumentList.Add(Path.GetFullPath(previewFilePath));
        }
        return startInfo;
    }

    public string PreparePreviewCopy(string sourceEpubPath, string bookName, string? previewRoot = null)
    {
        if (!File.Exists(sourceEpubPath)) throw new FileNotFoundException("找不到待预览的 EPUB。", sourceEpubPath);
        if (!string.Equals(Path.GetExtension(sourceEpubPath), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Kindle 设备预览当前使用 EPUB 作为官方转换输入。");

        var root = Path.GetFullPath(previewRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyPub Modern",
            "PreviewCache"));
        Directory.CreateDirectory(root);
        CleanupOldPreviewSessions(root);
        var sessionDirectory = Path.Combine(root, $"preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDirectory);
        var safeName = SanitizeFileName(bookName);
        var destination = Path.Combine(sessionDirectory, safeName + ".epub");
        File.Copy(sourceEpubPath, destination, overwrite: false);
        return destination;
    }

    private static IEnumerable<string> DefaultSearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Amazon",
            "Kindle Previewer 3");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Amazon");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Amazon");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Amazon");
        yield return @"D:\software\Kindle Previewer 3";
    }

    private static string? FindInRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return false;
                    var name = Path.GetFileNameWithoutExtension(path);
                    return name.Contains("kindlepreviewer", StringComparison.OrdinalIgnoreCase)
                           || name.Equals("Kindle Previewer", StringComparison.OrdinalIgnoreCase)
                           || name.Equals("Kindle Previewer 3", StringComparison.OrdinalIgnoreCase);
                })
                .Where(path => !Path.GetFileName(path).Contains("installer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals("kindlepreviewer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path =>
                {
                    var extension = Path.GetExtension(path);
                    return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                           || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1;
                })
                .ThenBy(path => path.Length)
                .ToArray();
            return files.Length == 0 ? null : Path.GetFullPath(files[0]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string SanitizeFileName(string bookName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string((bookName ?? string.Empty)
            .Where(character => !invalid.Contains(character) && !char.IsControl(character))
            .Take(80)
            .ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "Kindle预览" : safe;
    }

    private static void CleanupOldPreviewSessions(string root)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "preview-*", SearchOption.TopDirectoryOnly))
            {
                var fullPath = Path.GetFullPath(directory);
                var relative = Path.GetRelativePath(root, fullPath);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
                if (Directory.GetLastWriteTimeUtc(fullPath) >= cutoff) continue;
                try { Directory.Delete(fullPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
