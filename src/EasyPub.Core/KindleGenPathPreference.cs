namespace EasyPub.Core;

public static class KindleGenPathPreference
{
    private const string FileName = "kindlegen_v2.9.exe";

    public static string? ResolveForCurrentInstallation(string? savedPath, string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationBaseDirectory));
        var bundledPath = Path.Combine(baseDirectory, "bin", FileName);
        if (!File.Exists(bundledPath)) return NormalizeOptional(savedPath);

        if (string.IsNullOrWhiteSpace(savedPath)) return bundledPath;
        string fullSavedPath;
        try { fullSavedPath = Path.GetFullPath(savedPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return bundledPath;
        }

        if (string.Equals(fullSavedPath, bundledPath, StringComparison.OrdinalIgnoreCase))
            return bundledPath;
        if (!File.Exists(fullSavedPath)) return bundledPath;
        if (IsOlderVersionedPackagePath(fullSavedPath, baseDirectory)) return bundledPath;
        return fullSavedPath;
    }

    private static bool IsOlderVersionedPackagePath(string path, string currentBaseDirectory)
    {
        var binDirectory = Directory.GetParent(path);
        var packageDirectory = binDirectory?.Parent;
        if (packageDirectory is null ||
            !packageDirectory.Name.StartsWith("EasyPubModern-v", StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.Equals(
            Path.TrimEndingDirectorySeparator(packageDirectory.FullName),
            currentBaseDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptional(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return path; }
    }
}
