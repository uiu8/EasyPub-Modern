namespace EasyPub.Core;

public enum OutputCollisionPolicy
{
    AutoRename,
    Overwrite,
    Skip,
}

public sealed record OutputPathDecision(string? Path, bool Skipped, string Message);

public static class OutputPathPolicy
{
    public static OutputPathDecision Resolve(string requestedPath, OutputCollisionPolicy policy, ISet<string>? reservedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        var fullPath = Path.GetFullPath(requestedPath);
        reservedPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupied = File.Exists(fullPath) || reservedPaths.Contains(fullPath);
        if (!occupied)
        {
            reservedPaths.Add(fullPath);
            return new OutputPathDecision(fullPath, false, "使用原文件名");
        }

        if (policy == OutputCollisionPolicy.Skip)
            return new OutputPathDecision(null, true, $"已跳过现有文件：{Path.GetFileName(fullPath)}");
        if (policy == OutputCollisionPolicy.Overwrite)
        {
            reservedPaths.Add(fullPath);
            return new OutputPathDecision(fullPath, false, "覆盖现有文件");
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        for (var index = 2; index < 100_000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (File.Exists(candidate) || reservedPaths.Contains(candidate)) continue;
            reservedPaths.Add(candidate);
            return new OutputPathDecision(candidate, false, $"自动编号为 {Path.GetFileName(candidate)}");
        }
        throw new IOException($"无法为输出文件生成可用名称：{fullPath}");
    }
}
