using System.Buffers.Binary;
using System.Text;

namespace DiskStats.Core.Scanning;

public sealed record NtfsName(ulong Parent, string Name);
public readonly record struct NtfsDataRun(long Vcn, long Lcn, long Clusters);
public sealed record NtfsFileRecord(ulong Reference, bool Directory, FileAttributes Attributes,
    long Size, long Allocation, long Modified, uint Links, IReadOnlyList<NtfsName> Names,
    IReadOnlyList<NtfsDataRun> DataRuns, bool HasAttributeList);

/// <summary>Bounds-checked parser for NTFS FILE records, including update-sequence fixups and data runs.</summary>
public static class NtfsRecordParser
{
    public static NtfsFileRecord? Parse(ReadOnlySpan<byte> raw, ulong index, int sectorSize)
    {
        if (raw.Length >= 4 && raw[..4].SequenceEqual("BAAD"u8)) throw new InvalidDataException("Damaged NTFS record.");
        if (raw.Length < 48 || !raw[..4].SequenceEqual("FILE"u8)) return null;
        byte[] repaired = raw.ToArray();
        Span<byte> record = repaired;
        int fixup = U16(record, 4), fixups = U16(record, 6);
        if (sectorSize < 256 || fixups != record.Length / sectorSize + 1 || fixup < 8 || fixup + fixups * 2 > record.Length)
            throw new InvalidDataException("Invalid NTFS update-sequence array.");
        ushort sequence = U16(record, fixup);
        for (int i = 1; i < fixups; i++)
        {
            int end = i * sectorSize - 2;
            ushort original = U16(record, fixup + i * 2);
            ushort current = U16(record, end);
            if (current != sequence && current != original) throw new InvalidDataException("Torn NTFS record.");
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(end, 2), original);
        }
        ushort flags = U16(record, 22);
        if ((flags & 1) == 0 || U64(record, 32) != 0) return null; // unused or extension record
        bool directory = (flags & 2) != 0;
        ulong reference = (index & 0x0000ffffffffffffUL) | ((ulong)U16(record, 16) << 48);
        int used = checked((int)U32(record, 24));
        int offset = U16(record, 20);
        if (used > record.Length || offset < 48 || offset > used) throw new InvalidDataException("Invalid NTFS record bounds.");
        var names = new List<NtfsName>();
        var runs = new List<NtfsDataRun>();
        long size = 0, allocation = 0, modified = 0;
        FileAttributes attributes = directory ? FileAttributes.Directory : FileAttributes.Normal;
        bool attributeList = false;
        while (offset + 8 <= used)
        {
            uint type = U32(record, offset);
            if (type == 0xffffffff) break;
            int length = checked((int)U32(record, offset + 4));
            if (length < 24 || length > used - offset) throw new InvalidDataException("Invalid NTFS attribute length.");
            ReadOnlySpan<byte> attribute = record.Slice(offset, length);
            bool nonresident = attribute[8] != 0;
            int nameLength = attribute[9];
            if (type == 0x20) attributeList = true;
            if (!nonresident)
            {
                int valueLength = checked((int)U32(attribute, 16));
                int valueOffset = U16(attribute, 20);
                if (valueOffset < 24 || valueOffset > length || valueLength > length - valueOffset)
                    throw new InvalidDataException("Invalid resident NTFS attribute.");
                ReadOnlySpan<byte> value = attribute.Slice(valueOffset, valueLength);
                if (type == 0x10 && value.Length >= 36)
                {
                    modified = Timestamp(I64(value, 8));
                    attributes = (FileAttributes)U32(value, 32);
                }
                else if (type == 0x30 && value.Length >= 66)
                {
                    int chars = value[64];
                    if (66 + chars * 2 > value.Length) throw new InvalidDataException("Invalid NTFS filename.");
                    if (value[65] != 2) // a DOS alias is not another hardlink
                    {
                        string name = Encoding.Unicode.GetString(value.Slice(66, chars * 2));
                        if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || name is "." or "..")
                        { offset += length; continue; }
                        var entry = new NtfsName(U64(value, 0), name);
                        if (!names.Contains(entry)) names.Add(entry);
                    }
                }
                else if (type == 0x80 && nameLength == 0) size = valueLength;
            }
            else if (type == 0x80 && nameLength == 0)
            {
                if (length < 64) throw new InvalidDataException("Invalid nonresident NTFS data.");
                long vcn = I64(attribute, 16);
                if (vcn == 0)
                {
                    allocation = I64(attribute, 40);
                    size = I64(attribute, 48);
                    if ((U16(attribute, 12) & 0x8001) != 0)
                        allocation = length >= 72 ? I64(attribute, 64) : -1;
                }
                int runOffset = U16(attribute, 32);
                if (runOffset < 64 || runOffset >= length) throw new InvalidDataException("Invalid NTFS run offset.");
                runs.AddRange(ParseRuns(attribute[runOffset..], vcn));
            }
            offset += length;
        }
        if (size < 0 || allocation < -1) throw new InvalidDataException("Invalid NTFS data sizes.");
        return new NtfsFileRecord(reference, directory, attributes, size, allocation, modified,
            U16(record, 18), names, runs, attributeList);
    }

    public static IReadOnlyList<NtfsDataRun> ParseRuns(ReadOnlySpan<byte> bytes, long firstVcn = 0)
    {
        var runs = new List<NtfsDataRun>();
        long vcn = firstVcn, lcn = 0;
        int offset = 0;
        while (offset < bytes.Length && bytes[offset] != 0)
        {
            byte header = bytes[offset++];
            int countBytes = header & 15, deltaBytes = header >> 4;
            if (countBytes is < 1 or > 8 || deltaBytes > 8 || offset + countBytes + deltaBytes > bytes.Length)
                throw new InvalidDataException("Invalid NTFS data run.");
            ulong clusters = 0;
            for (int i = 0; i < countBytes; i++) clusters |= (ulong)bytes[offset++] << (8 * i);
            if (clusters == 0 || clusters > long.MaxValue) throw new InvalidDataException("Invalid NTFS run length.");
            long runLcn = -1;
            if (deltaBytes != 0)
            {
                ulong delta = 0;
                for (int i = 0; i < deltaBytes; i++) delta |= (ulong)bytes[offset++] << (8 * i);
                if (deltaBytes < 8 && (delta & (1UL << (deltaBytes * 8 - 1))) != 0) delta |= ulong.MaxValue << (deltaBytes * 8);
                lcn = checked(lcn + unchecked((long)delta));
                if (lcn < 0) throw new InvalidDataException("Negative NTFS cluster address.");
                runLcn = lcn;
            }
            runs.Add(new NtfsDataRun(vcn, runLcn, (long)clusters));
            vcn = checked(vcn + (long)clusters);
        }
        return runs;
    }

    private static long Timestamp(long filetime)
    {
        try { return DateTime.FromFileTimeUtc(filetime).Ticks; }
        catch (ArgumentOutOfRangeException) { return 0; }
    }
    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o, 2));
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o, 4));
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(o, 8));
    private static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b.Slice(o, 8));
}
