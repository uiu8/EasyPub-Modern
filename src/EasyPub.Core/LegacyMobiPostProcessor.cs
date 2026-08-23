using System.Buffers.Binary;
using System.Text;

namespace EasyPub.Core;

internal static class LegacyMobiPostProcessor
{
    private const string RemoveMarker = "_EASYPUB_REMOVE_";

    public static byte[] StripSourceArchive(byte[] source)
    {
        if (source.Length < 92 || Encoding.ASCII.GetString(source, 60, 8) != "BOOKMOBI")
            throw new InvalidDataException("KindleGen 输出不是有效的 BOOKMOBI 文件。");

        var recordCount = ReadUInt16(source, 76);
        var record0Offset = checked((int)ReadUInt32(source, 78));
        var record1Offset = checked((int)ReadUInt32(source, 86));
        var record0 = Slice(source, record0Offset, record1Offset);
        if (record0.Length < 232) return source;

        var sourceRecord = checked((int)ReadUInt32(record0, 224));
        var sourceRecordCount = checked((int)ReadUInt32(record0, 228));
        if (sourceRecord == -1 || sourceRecordCount == 0) return source;
        var afterSourceRecord = sourceRecord + sourceRecordCount;
        if (sourceRecord < 0 || afterSourceRecord >= recordCount) return source;

        var sourceOffset = GetRecordOffset(source, sourceRecord);
        var afterSourceOffset = GetRecordOffset(source, afterSourceRecord);
        var removedBytes = afterSourceOffset - sourceOffset;
        if (removedBytes <= 0 || Encoding.ASCII.GetString(source, sourceOffset, 4) != "SRCS") return source;

        var newRecordCount = recordCount - sourceRecordCount;
        var result = new List<byte>(source.Length - removedBytes - sourceRecordCount * 8);
        result.AddRange(Slice(source, 0, 68));
        result.AddRange(ToBigEndian((uint)(newRecordCount * 2 + 1)));
        result.AddRange(Slice(source, 72, 76));
        result.AddRange(ToBigEndian((ushort)newRecordCount));

        var recordTableBytesRemoved = sourceRecordCount * 8;
        for (var index = 0; index < sourceRecord; index++)
        {
            var offset = GetRecordOffset(source, index) - recordTableBytesRemoved;
            result.AddRange(ToBigEndian((uint)offset));
            result.AddRange(Slice(source, 82 + index * 8, 86 + index * 8));
        }
        for (var index = afterSourceRecord; index < recordCount; index++)
        {
            var offset = GetRecordOffset(source, index) - recordTableBytesRemoved - removedBytes;
            result.AddRange(ToBigEndian((uint)offset));
            result.AddRange(ToBigEndian((uint)(2 * (index - sourceRecordCount))));
        }

        var firstRecordOffset = checked((int)ReadUInt32(CollectionsMarshalAsSpan(result), 78));
        while (result.Count < firstRecordOffset) result.Add(0);
        result.AddRange(Slice(source, record0Offset, sourceOffset));
        result.AddRange(Slice(source, afterSourceOffset, source.Length));

        var bytes = result.ToArray();
        var newRecord0Offset = checked((int)ReadUInt32(bytes, 78));
        var newRecord1Offset = checked((int)ReadUInt32(bytes, 86));
        var newRecord0 = Slice(bytes, newRecord0Offset, newRecord1Offset);
        WriteUInt32(newRecord0, 224, uint.MaxValue);
        WriteUInt32(newRecord0, 228, 0);
        AdjustKf8BoundaryRecord(newRecord0, sourceRecord, sourceRecordCount);
        Buffer.BlockCopy(newRecord0, 0, bytes, newRecord0Offset, newRecord0.Length);
        return bytes;
    }

    private static void AdjustKf8BoundaryRecord(byte[] record0, int removedAtRecord, int removedRecordCount)
    {
        var mobiHeaderLength = checked((int)ReadUInt32(record0, 20));
        var flags = ReadUInt32(record0, 128);
        if ((flags & 0x40) == 0) return;

        var exthOffset = 16 + mobiHeaderLength;
        if (exthOffset + 12 > record0.Length || Encoding.ASCII.GetString(record0, exthOffset, 4) != "EXTH")
            return;

        var count = checked((int)ReadUInt32(record0, exthOffset + 8));
        var cursor = exthOffset + 12;
        for (var index = 0; index < count; index++)
        {
            if (cursor + 8 > record0.Length) return;
            var type = ReadUInt32(record0, cursor);
            var length = checked((int)ReadUInt32(record0, cursor + 4));
            if (length < 8 || cursor + length > record0.Length) return;
            if (type == 121 && length >= 12)
            {
                var boundaryRecord = checked((int)ReadUInt32(record0, cursor + 8));
                if (boundaryRecord >= removedAtRecord)
                    WriteUInt32(record0, cursor + 8, checked((uint)(boundaryRecord - removedRecordCount)));
                return;
            }
            cursor += length;
        }
    }

    public static byte[] ApplyEasyPubMetadata(byte[] source, string? asin)
    {
        var replacements = new Dictionary<int, string> { [300] = RemoveMarker };
        if (!string.IsNullOrWhiteSpace(asin))
        {
            replacements[113] = asin;
            replacements[501] = "EBOK";
        }
        var primary = RewritePrimaryHeader(source, replacements);
        if (primary.Length == 1)
            throw new InvalidDataException("无法处理主 MOBI EXTH 记录。");
        var secondary = RewriteKf8Header(primary, replacements);
        if (secondary.Length == 1)
            throw new InvalidDataException("无法处理 KF8 EXTH 记录。");
        return secondary;
    }

    public static bool HasValidJointStructure(byte[] source)
    {
        try
        {
            if (source.Length < 92 || Encoding.ASCII.GetString(source, 60, 8) != "BOOKMOBI") return false;
            var recordCount = ReadUInt16(source, 76);
            var boundaryRecord = -1;
            for (var index = 0; index < recordCount - 1; index++)
            {
                var start = GetRecordOffset(source, index);
                var end = GetRecordOffset(source, index + 1);
                if (end - start == 8 && Encoding.ASCII.GetString(source, start, 8) == "BOUNDARY")
                {
                    boundaryRecord = index;
                    break;
                }
            }
            if (boundaryRecord < 0) return false;

            var record0 = GetRecordOffset(source, 0);
            var mobiHeaderLength = checked((int)ReadUInt32(source, record0 + 20));
            var exth = record0 + 16 + mobiHeaderLength;
            if (exth + 12 > source.Length || Encoding.ASCII.GetString(source, exth, 4) != "EXTH") return false;
            var count = checked((int)ReadUInt32(source, exth + 8));
            var cursor = exth + 12;
            var declaredKf8Record = -1;
            for (var index = 0; index < count; index++)
            {
                var type = ReadUInt32(source, cursor);
                var length = checked((int)ReadUInt32(source, cursor + 4));
                if (length < 8 || cursor + length > source.Length) return false;
                if (type == 121 && length >= 12)
                    declaredKf8Record = checked((int)ReadUInt32(source, cursor + 8));
                cursor += length;
            }
            if (declaredKf8Record != boundaryRecord + 1) return false;
            var kf8 = GetRecordOffset(source, declaredKf8Record);
            return kf8 + 20 <= source.Length && Encoding.ASCII.GetString(source, kf8 + 16, 4) == "MOBI";
        }
        catch (Exception) when (source.Length > 0)
        {
            return false;
        }
    }

    private static byte[] RewritePrimaryHeader(byte[] source, IReadOnlyDictionary<int, string> replacements)
    {
        if (source.Length < 92 || Encoding.ASCII.GetString(source, 60, 8) != "BOOKMOBI") return [0];
        try
        {
            var recordCount = ReadUInt16(source, 76);
            var recordStart = checked((int)ReadUInt32(source, 78));
            var recordEnd = checked((int)ReadUInt32(source, 86));
            var rewritten = RewriteRecord(Slice(source, recordStart, recordEnd), replacements);
            if (rewritten is null) return [0];
            return ReplaceRecordAndOffsets(source, recordCount, 0, recordStart, recordEnd, rewritten);
        }
        catch (Exception) when (source.Length > 0)
        {
            return [0];
        }
    }

    private static byte[] RewriteKf8Header(byte[] source, IReadOnlyDictionary<int, string> replacements)
    {
        if (source.Length < 92 || Encoding.ASCII.GetString(source, 60, 8) != "BOOKMOBI") return [0];
        var recordCount = ReadUInt16(source, 76);
        for (var index = 0; index < recordCount - 1; index++)
        {
            var start = GetRecordOffset(source, index);
            var end = GetRecordOffset(source, index + 1);
            if (end - start != 8 || Encoding.ASCII.GetString(source, start, 8) != "BOUNDARY") continue;

            var kf8Index = index + 1;
            if (kf8Index >= recordCount - 1) return source;
            var recordStart = GetRecordOffset(source, kf8Index);
            var recordEnd = GetRecordOffset(source, kf8Index + 1);
            var rewritten = RewriteRecord(Slice(source, recordStart, recordEnd), replacements);
            return rewritten is null
                ? source
                : ReplaceRecordAndOffsets(source, recordCount, kf8Index, recordStart, recordEnd, rewritten);
        }
        return source;
    }

    private static byte[]? RewriteRecord(byte[] record, IReadOnlyDictionary<int, string> replacements)
    {
        var mobiHeaderLength = checked((int)ReadUInt32(record, 20));
        var flags = checked((int)ReadUInt32(record, 128));
        var titleOffset = checked((int)ReadUInt32(record, 84));
        var titleLength = checked((int)ReadUInt32(record, 88));
        var title = Slice(record, titleOffset, titleOffset + titleLength);
        if (titleLength % 4 != 0) title = Concat(title, new byte[4 - titleLength % 4]);
        if ((flags & 0x40) == 0) return null;

        var exthOffset = 16 + mobiHeaderLength;
        var exth = Slice(record, exthOffset, record.Length);
        if (exth.Length < 12 || Encoding.ASCII.GetString(exth, 0, 4) != "EXTH") return null;
        var oldCount = checked((int)ReadUInt32(exth, 8));
        var newCount = oldCount;
        var entries = new List<byte>(exth.Length);
        entries.AddRange(Slice(exth, 0, 12));
        var cursor = 12;
        var pending = new Dictionary<int, string>(replacements);
        for (var index = 0; index < oldCount; index++)
        {
            var type = checked((int)ReadUInt32(exth, cursor));
            var length = checked((int)ReadUInt32(exth, cursor + 4));
            if (!pending.TryGetValue(type, out var replacement))
            {
                entries.AddRange(Slice(exth, cursor, cursor + length));
            }
            else if (replacement == RemoveMarker)
            {
                newCount--;
                pending.Remove(type);
            }
            else
            {
                var value = Encoding.UTF8.GetBytes(replacement);
                entries.AddRange(ToBigEndian((uint)type));
                entries.AddRange(ToBigEndian((uint)(value.Length + 8)));
                entries.AddRange(value);
                pending.Remove(type);
            }
            cursor += length;
        }
        foreach (var item in pending)
        {
            var value = Encoding.UTF8.GetBytes(item.Value);
            entries.AddRange(ToBigEndian((uint)item.Key));
            entries.AddRange(ToBigEndian((uint)(value.Length + 8)));
            entries.AddRange(value);
            newCount++;
        }
        while (entries.Count % 4 != 0) entries.Add(0);
        var rewrittenExth = entries.ToArray();
        WriteUInt32(rewrittenExth, 4, (uint)rewrittenExth.Length);
        WriteUInt32(rewrittenExth, 8, (uint)newCount);

        var prefix = Slice(record, 0, exthOffset);
        var rewritten = Concat(prefix, rewrittenExth, title, new byte[4]);
        WriteUInt32(rewritten, 84, (uint)(prefix.Length + rewrittenExth.Length));
        return rewritten;
    }

    private static byte[] ReplaceRecordAndOffsets(
        byte[] source,
        int recordCount,
        int recordIndex,
        int recordStart,
        int recordEnd,
        byte[] replacement)
    {
        var delta = replacement.Length - (recordEnd - recordStart);
        var result = Concat(Slice(source, 0, recordStart), replacement);
        for (var index = recordIndex + 1; index < recordCount; index++)
        {
            var oldOffset = checked((int)ReadUInt32(source, 78 + index * 8));
            WriteUInt32(result, 78 + index * 8, (uint)(oldOffset + delta));
        }
        return Concat(result, Slice(source, recordEnd, source.Length));
    }

    private static int GetRecordOffset(byte[] bytes, int index) =>
        checked((int)ReadUInt32(bytes, 78 + index * 8));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));

    private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.Slice(offset, 4), value);

    private static byte[] Slice(byte[] bytes, int start, int end) => bytes[start..end];
    private static byte[] Concat(params byte[][] arrays)
    {
        var length = arrays.Sum(array => array.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }
        return result;
    }
    private static byte[] ToBigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
    private static byte[] ToBigEndian(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> bytes) => bytes.ToArray();
}
