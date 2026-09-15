using System.Security.Cryptography;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>Records the exact decoded source and cleanup pass used by the writer, before output is committed.</summary>
internal sealed class PublicationChangeReceipt
{
    private readonly string _path;
    private readonly ConversionRequest _request;
    private readonly object _changes;
    private PublicationChangeReceipt(string path, ConversionRequest request, object changes)
    { _path = path; _request = request; _changes = changes; }

    public static PublicationChangeReceipt? Prepare(ConversionRequest request, byte[] bytes, TextCleanupPreview cleanup)
    {
        var options = request.Options ?? ConversionOptions.LegacyDefault;
        if (request.ChapterTree is null && !cleanup.Changes.Any(c => c.IsApplied)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (request.ChapterTree is { } plan && !string.Equals(plan.SourceSha256, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("源 TXT 已变化，不能套用旧章节树或生成变更报告。");
        var original = TextFileDecoder.Decode(bytes, options.TextEncoding).Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var covered = request.ChapterTree is { } tree ? RepairIntegrity.Coverage(tree.Entries)
            : Enumerable.Range(1, original.Length).ToHashSet();
        var omitted = Enumerable.Range(1, original.Length).Where(line => !covered.Contains(line))
            .Select(line => new { Line = line, Text = original[line - 1] }).ToArray();
        // Preserve these exact input bytes, including their original encoding. The backup must be
        // complete before writing any transformed output, even when optional structure validation is off.
        var backupRoot = Path.Combine(SourceBackupStore.CreateDefault().DirectoryPath, "PublicationSources", hash);
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, Path.GetFileName(request.InputPath) + ".bak");
        if (!File.Exists(backup))
        {
            var temp = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            try { File.Move(temp, backup, false); }
            catch (IOException) when (File.Exists(backup)) { File.Delete(temp); }
        }
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))) != hash)
            throw new InvalidDataException("原文备份校验失败，已停止制作。");
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!, "EasyPub-变更记录");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(request.OutputPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
            + "." + Guid.NewGuid().ToString("N")[..8] + ".json");
        var evidence = new
        {
            SourcePath = Path.GetFullPath(request.InputPath), SourceSha256 = hash, BackupPath = backup,
            SourceLineCount = original.Length, OutputPath = Path.GetFullPath(request.OutputPath),
            OmittedSourceLines = omitted,
            CleanupChanges = cleanup.Changes,
            ChapterTree = request.ChapterTree,
            ParagraphFormatting = new { options.RemoveBlankLines, options.AddFullWidthIndent, options.FullWidthIndentCount },
            Scope = "记录本次实际解析所用的章节树及清理逐行变化。清理先于章节树取文；未被取用的行不进入成品。排版空白会按设置处理。本记录不代替成品结构或设备验收。"
        };
        var receipt = new PublicationChangeReceipt(path, request, evidence);
        receipt.Save("已备份，准备制作", null);
        return receipt;
    }

    public async Task CompleteAsync(CancellationToken token)
    {
        await using var output = File.OpenRead(_request.OutputPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, token).ConfigureAwait(false));
        Save("制作完成", hash);
    }

    private void Save(string state, string? outputHash)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { State = state, OutputSha256 = outputHash, RecordedAt = DateTimeOffset.UtcNow, Evidence = _changes },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, _path, true);
    }
}
