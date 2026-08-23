using System.Buffers.Binary;
using System.Text;

namespace EasyPub.Core;

public sealed record FontEmbeddingInfo(
    string FontPath,
    bool IsTrueType,
    bool CanEmbed,
    bool CanSubset,
    ushort LicenseFlags);

public sealed record PreparedEmbeddedFont(
    string FamilyName,
    byte[] Bytes,
    bool WasSubset);

public static class FontEmbeddingService
{
    public static FontEmbeddingInfo Inspect(string fontPath)
    {
        var path = Path.GetFullPath(fontPath);
        var bytes = File.ReadAllBytes(path);
        var font = TrueTypeFont.Parse(bytes);
        var flags = font.TryGetTable("OS/2", out var os2) && os2.Length >= 10
            ? ReadUInt16(os2, 8)
            : (ushort)0;
        var canEmbed = (flags & 0x0002) == 0 && (flags & 0x0200) == 0;
        var canSubset = (flags & 0x0100) == 0;
        return new FontEmbeddingInfo(path, true, canEmbed, canSubset, flags);
    }

    public static async Task<PreparedEmbeddedFont> PrepareAsync(
        EmbeddedFontOptions options,
        string usedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.FontPath))
            throw new ArgumentException("尚未选择要嵌入的字体。", nameof(options));

        var path = Path.GetFullPath(options.FontPath);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var font = TrueTypeFont.Parse(bytes);
        var flags = font.TryGetTable("OS/2", out var os2) && os2.Length >= 10
            ? ReadUInt16(os2, 8)
            : (ushort)0;
        if ((flags & 0x0002) != 0 || (flags & 0x0200) != 0)
            throw new InvalidOperationException("该字体的嵌入许可禁止用于电子书，请选择允许嵌入的字体。");

        var shouldSubset = options.Subset && (flags & 0x0100) == 0;
        var prepared = shouldSubset
            ? font.CreateSparseSubset(usedText, cancellationToken)
            : bytes;
        var familyName = string.IsNullOrWhiteSpace(options.FamilyName)
            ? Path.GetFileNameWithoutExtension(path)
            : options.FamilyName.Trim();
        return new PreparedEmbeddedFont(familyName, prepared, shouldSubset);
    }

    private sealed class TrueTypeFont
    {
        private readonly byte[] _bytes;
        private readonly Dictionary<string, TableRecord> _tables;

        private TrueTypeFont(byte[] bytes, Dictionary<string, TableRecord> tables)
        {
            _bytes = bytes;
            _tables = tables;
        }

        public static TrueTypeFont Parse(byte[] bytes)
        {
            if (bytes.Length < 12) throw new InvalidDataException("字体文件过短。");
            var scaler = ReadUInt32(bytes, 0);
            if (scaler == 0x74746366) throw new NotSupportedException("暂不支持 TTC 字体集合；请选择独立的 TTF 字体。");
            if (scaler == 0x4F54544F) throw new NotSupportedException("暂不支持 CFF/OTF 子集化；请选择 TrueType 轮廓的 TTF 字体。");
            if (scaler is not 0x00010000 and not 0x74727565)
                throw new InvalidDataException("不是受支持的 TrueType 字体。");

            var count = ReadUInt16(bytes, 4);
            if (12 + count * 16 > bytes.Length) throw new InvalidDataException("TrueType 表目录损坏。");
            var tables = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var offset = 12 + index * 16;
                var tag = Encoding.ASCII.GetString(bytes, offset, 4);
                var tableOffset = checked((int)ReadUInt32(bytes, offset + 8));
                var length = checked((int)ReadUInt32(bytes, offset + 12));
                if (tableOffset < 0 || length < 0 || tableOffset + length > bytes.Length)
                    throw new InvalidDataException($"TrueType 表 {tag} 超出文件范围。");
                tables[tag] = new TableRecord(tag, tableOffset, length);
            }
            foreach (var required in new[] { "head", "maxp", "loca", "glyf", "cmap" })
                if (!tables.ContainsKey(required))
                    throw new NotSupportedException($"字体缺少 {required} 表，可能不是 TrueType glyf 字体。");
            return new TrueTypeFont(bytes, tables);
        }

        public bool TryGetTable(string tag, out ReadOnlySpan<byte> table)
        {
            if (_tables.TryGetValue(tag, out var record))
            {
                table = _bytes.AsSpan(record.Offset, record.Length);
                return true;
            }
            table = default;
            return false;
        }

        public byte[] CreateSparseSubset(string usedText, CancellationToken cancellationToken)
        {
            var head = GetTable("head");
            var maxp = GetTable("maxp");
            var loca = GetTable("loca");
            var glyf = GetTable("glyf");
            if (head.Length < 54 || maxp.Length < 6) throw new InvalidDataException("TrueType 关键表损坏。");
            var glyphCount = ReadUInt16(maxp, 4);
            var longLoca = ReadInt16(head, 50) != 0;
            var requiredLocaLength = (glyphCount + 1) * (longLoca ? 4 : 2);
            if (loca.Length < requiredLocaLength) throw new InvalidDataException("TrueType loca 表损坏。");

            var offsets = new uint[glyphCount + 1];
            for (var index = 0; index <= glyphCount; index++)
                offsets[index] = longLoca ? ReadUInt32(loca, index * 4) : (uint)ReadUInt16(loca, index * 2) * 2;
            if (offsets[^1] > glyf.Length) throw new InvalidDataException("TrueType glyf/loca 范围无效。");

            var selected = new HashSet<ushort> { 0 };
            foreach (var rune in usedText.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var glyph = MapCodePoint(rune.Value);
                if (glyph < glyphCount) selected.Add(glyph);
            }

            var queue = new Queue<ushort>(selected);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var glyph = queue.Dequeue();
                foreach (var component in ReadCompositeComponents(glyph, offsets, glyf))
                    if (component < glyphCount && selected.Add(component)) queue.Enqueue(component);
            }

            using var glyfOutput = new MemoryStream();
            var newOffsets = new uint[glyphCount + 1];
            for (ushort glyph = 0; glyph < glyphCount; glyph++)
            {
                newOffsets[glyph] = checked((uint)glyfOutput.Length);
                if (!selected.Contains(glyph)) continue;
                var start = offsets[glyph];
                var length = offsets[glyph + 1] - start;
                if (length > 0) glyfOutput.Write(glyf.Slice(checked((int)start), checked((int)length)));
                while ((glyfOutput.Length & 3) != 0) glyfOutput.WriteByte(0);
            }
            newOffsets[glyphCount] = checked((uint)glyfOutput.Length);

            var newLoca = new byte[requiredLocaLength];
            for (var index = 0; index <= glyphCount; index++)
            {
                if (longLoca)
                    WriteUInt32(newLoca, index * 4, newOffsets[index]);
                else
                    WriteUInt16(newLoca, index * 2, checked((ushort)(newOffsets[index] / 2)));
            }

            var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["glyf"] = glyfOutput.ToArray(),
                ["loca"] = newLoca,
                ["head"] = GetTable("head").ToArray(),
            };
            replacements["head"].AsSpan(8, 4).Clear();
            return Rebuild(replacements);
        }

        private ushort MapCodePoint(int codePoint)
        {
            var cmap = GetTable("cmap");
            if (cmap.Length < 4) throw new InvalidDataException("TrueType cmap 表损坏。");
            var count = ReadUInt16(cmap, 2);
            var candidates = new List<(int Rank, int Offset)>();
            for (var index = 0; index < count; index++)
            {
                var record = 4 + index * 8;
                if (record + 8 > cmap.Length) break;
                var platform = ReadUInt16(cmap, record);
                var encoding = ReadUInt16(cmap, record + 2);
                var offset = checked((int)ReadUInt32(cmap, record + 4));
                if (offset + 2 > cmap.Length) continue;
                var format = ReadUInt16(cmap, offset);
                var rank = format == 12 && platform == 3 && encoding == 10 ? 0
                    : format == 12 && platform == 0 ? 1
                    : format == 4 && platform == 3 ? 2
                    : format == 4 && platform == 0 ? 3
                    : 99;
                if (rank < 99) candidates.Add((rank, offset));
            }

            foreach (var candidate in candidates.OrderBy(item => item.Rank))
            {
                var format = ReadUInt16(cmap, candidate.Offset);
                var glyph = format == 12
                    ? MapFormat12(cmap, candidate.Offset, codePoint)
                    : MapFormat4(cmap, candidate.Offset, codePoint);
                if (glyph != 0) return glyph;
            }
            return 0;
        }

        private static ushort MapFormat12(ReadOnlySpan<byte> cmap, int offset, int codePoint)
        {
            if (offset + 16 > cmap.Length) return 0;
            var groups = checked((int)ReadUInt32(cmap, offset + 12));
            var low = 0;
            var high = groups - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var groupOffset = offset + 16 + middle * 12;
                if (groupOffset + 12 > cmap.Length) return 0;
                var start = ReadUInt32(cmap, groupOffset);
                var end = ReadUInt32(cmap, groupOffset + 4);
                if ((uint)codePoint < start) high = middle - 1;
                else if ((uint)codePoint > end) low = middle + 1;
                else return checked((ushort)(ReadUInt32(cmap, groupOffset + 8) + (uint)codePoint - start));
            }
            return 0;
        }

        private static ushort MapFormat4(ReadOnlySpan<byte> cmap, int offset, int codePoint)
        {
            if (codePoint > ushort.MaxValue || offset + 16 > cmap.Length) return 0;
            var length = ReadUInt16(cmap, offset + 2);
            var end = Math.Min(cmap.Length, offset + length);
            var segmentCount = ReadUInt16(cmap, offset + 6) / 2;
            var endCodes = offset + 14;
            var startCodes = endCodes + segmentCount * 2 + 2;
            var deltas = startCodes + segmentCount * 2;
            var rangeOffsets = deltas + segmentCount * 2;
            for (var index = 0; index < segmentCount; index++)
            {
                if (rangeOffsets + index * 2 + 2 > end) return 0;
                var segmentEnd = ReadUInt16(cmap, endCodes + index * 2);
                var segmentStart = ReadUInt16(cmap, startCodes + index * 2);
                if (codePoint < segmentStart || codePoint > segmentEnd) continue;
                var delta = ReadInt16(cmap, deltas + index * 2);
                var range = ReadUInt16(cmap, rangeOffsets + index * 2);
                if (range == 0) return (ushort)((codePoint + delta) & 0xffff);
                var glyphOffset = rangeOffsets + index * 2 + range + (codePoint - segmentStart) * 2;
                if (glyphOffset + 2 > end) return 0;
                var glyph = ReadUInt16(cmap, glyphOffset);
                return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xffff);
            }
            return 0;
        }

        private static IReadOnlyList<ushort> ReadCompositeComponents(
            ushort glyph,
            IReadOnlyList<uint> offsets,
            ReadOnlySpan<byte> glyf)
        {
            var result = new List<ushort>();
            var start = checked((int)offsets[glyph]);
            var end = checked((int)offsets[glyph + 1]);
            if (end - start < 10 || ReadInt16(glyf, start) >= 0) return result;
            var cursor = start + 10;
            ushort flags;
            do
            {
                if (cursor + 4 > end) throw new InvalidDataException("TrueType 复合字形损坏。");
                flags = ReadUInt16(glyf, cursor);
                var component = ReadUInt16(glyf, cursor + 2);
                result.Add(component);
                cursor += 4;
                cursor += (flags & 0x0001) != 0 ? 4 : 2;
                cursor += (flags & 0x0008) != 0 ? 2
                    : (flags & 0x0040) != 0 ? 4
                    : (flags & 0x0080) != 0 ? 8
                    : 0;
            } while ((flags & 0x0020) != 0);
            return result;
        }

        private byte[] Rebuild(IReadOnlyDictionary<string, byte[]> replacements)
        {
            var tables = _tables.Keys.OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
            var tableData = tables.ToDictionary(
                tag => tag,
                tag => replacements.TryGetValue(tag, out var replacement)
                    ? replacement
                    : GetTable(tag).ToArray(),
                StringComparer.Ordinal);
            var count = tables.Length;
            var headerLength = 12 + count * 16;
            var totalLength = headerLength + tableData.Values.Sum(data => Align4(data.Length));
            var output = new byte[totalLength];
            WriteUInt32(output, 0, 0x00010000);
            WriteUInt16(output, 4, checked((ushort)count));
            var maxPower = 1;
            var entrySelector = 0;
            while (maxPower * 2 <= count) { maxPower *= 2; entrySelector++; }
            WriteUInt16(output, 6, checked((ushort)(maxPower * 16)));
            WriteUInt16(output, 8, checked((ushort)entrySelector));
            WriteUInt16(output, 10, checked((ushort)(count * 16 - maxPower * 16)));

            var cursor = headerLength;
            var written = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
            for (var index = 0; index < tables.Length; index++)
            {
                var tag = tables[index];
                var data = tableData[tag];
                var record = 12 + index * 16;
                Encoding.ASCII.GetBytes(tag, output.AsSpan(record, 4));
                WriteUInt32(output, record + 4, CalculateChecksum(data));
                WriteUInt32(output, record + 8, checked((uint)cursor));
                WriteUInt32(output, record + 12, checked((uint)data.Length));
                data.CopyTo(output, cursor);
                written[tag] = (cursor, data.Length);
                cursor += Align4(data.Length);
            }

            var adjustment = 0xB1B0AFBAu - CalculateChecksum(output);
            WriteUInt32(output, written["head"].Offset + 8, adjustment);
            return output;
        }

        private ReadOnlySpan<byte> GetTable(string tag)
        {
            var table = _tables[tag];
            return _bytes.AsSpan(table.Offset, table.Length);
        }

        private sealed record TableRecord(string Tag, int Offset, int Length);
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static uint CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        Span<byte> word = stackalloc byte[4];
        for (var index = 0; index < data.Length; index += 4)
        {
            word.Clear();
            data.Slice(index, Math.Min(4, data.Length - index)).CopyTo(word);
            sum += BinaryPrimitives.ReadUInt32BigEndian(word);
        }
        return sum;
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    private static void WriteUInt16(Span<byte> data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(offset, 2), value);
    private static void WriteUInt32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(offset, 4), value);
}
