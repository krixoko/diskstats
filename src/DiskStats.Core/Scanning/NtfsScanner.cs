using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NativeFileHandle = Microsoft.Win32.SafeHandles.SafeFileHandle;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Scanning;

/// <summary>Reads the MFT in sequential blocks from a read-only volume handle.</summary>
public static class NtfsScanner
{
    public static bool TryScan(string root, WalkOptions options, CancellationToken cancel,
        out NodeBuffer? buffer, out string? unavailable)
        => TryScan(root, options, cancel, new ScanErrorLog(), out buffer, out unavailable);

    /// <summary>
    /// Wie viele einzelne beschaedigte MFT-Records uebersprungen werden duerfen, bevor der
    /// ganze Scan als unglaubwuerdig gilt und der Verzeichnisscan uebernimmt.
    /// </summary>
    private const int MaxDamagedRecords = 1000;

    public static bool TryScan(string root, WalkOptions options, CancellationToken cancel, ScanErrorLog errors,
        out NodeBuffer? buffer, out string? unavailable)
    {
        buffer = null;
        unavailable = null;
        int damaged = 0;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("NTFS scanning requires Windows.");
            root = Path.GetFullPath(root);
            string drive = Path.GetPathRoot(root)!;
            if (drive.Length != 3 || new DriveInfo(drive).DriveFormat != "NTFS")
                throw new NotSupportedException("Direct scanning requires a local NTFS volume.");
            using NativeFileHandle volume = CreateFileW(@"\\.\" + drive[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] data = Control(volume, 0x90064, [], 128);
            if (data.Length < 96) throw new InvalidDataException("Truncated NTFS volume information.");
            ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(data);
            int sector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40)));
            int cluster = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44)));
            int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48)));
            long validLength = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56));
            if (recordSize < 512 || recordSize > 65536 || cluster < 512 || validLength < recordSize)
                throw new InvalidDataException("Invalid NTFS geometry.");
            NtfsFileRecord mft = NtfsRecordParser.Parse(ReadRecord(volume, 0, recordSize).Bytes, 0, sector)
                ?? throw new InvalidDataException("MFT record unavailable.");
            var records = new Dictionary<ulong, NtfsFileRecord>();
            long covered = 0;
            foreach (NtfsDataRun run in mft.DataRuns.OrderBy(r => r.Vcn))
            {
                if (run.Lcn < 0 || checked(run.Vcn * cluster) != covered) break;
                covered = checked(covered + run.Clusters * cluster);
            }
            if (covered >= validLength && cluster % recordSize == 0)
            {
                byte[] block = new byte[Math.Max(1024 * 1024, recordSize)];
                foreach (NtfsDataRun run in mft.DataRuns.OrderBy(r => r.Vcn))
                {
                    long logical = checked(run.Vcn * cluster);
                    long length = Math.Min(checked(run.Clusters * cluster), validLength - logical);
                    for (long offset = 0; offset < length;)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int take = (int)Math.Min(block.Length, length - offset);
                        take -= take % recordSize;
                        if (take == 0) break;
                        ReadExactly(volume, block.AsSpan(0, take), checked(run.Lcn * cluster + offset));
                        for (int p = 0; p < take; p += recordSize)
                        {
                            ulong index = (ulong)((logical + offset + p) / recordSize);
                            // Ein einzelner zerrissener Record (gerade im Schreiben, oder BAAD)
                            // kostet einen Eintrag, nicht den Scan. Haeufen sie sich, stimmt
                            // etwas Grundsaetzliches nicht — dann uebernimmt der Verzeichnisscan.
                            NtfsFileRecord? record;
                            try { record = NtfsRecordParser.Parse(block.AsSpan(p, recordSize), index, sector); }
                            catch (InvalidDataException)
                            {
                                errors.Record($"MFT #{index}", unchecked((int)0x80070570)); // ERROR_FILE_CORRUPT
                                if (++damaged > MaxDamagedRecords)
                                    throw new InvalidDataException($"{damaged} beschaedigte MFT-Records.");
                                continue;
                            }
                            if (record is not null) records[record.Reference] = record;
                        }
                        offset += take;
                    }
                    if (logical + length >= validLength) break;
                }
            }
            else
            {
                // Fragmented MFT attribute lists may not fit in the base record. The filesystem
                // can retrieve each complete FILE record without interpreting those extents here.
                ulong index = checked((ulong)(validLength / recordSize - 1));
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var record = ReadRecord(volume, index, recordSize);
                    if (record.Index > index) throw new InvalidDataException("Unexpected NTFS record order.");
                    NtfsFileRecord? parsed = NtfsRecordParser.Parse(record.Bytes, record.Index, sector);
                    if (parsed is not null) records[parsed.Reference] = parsed;
                    if (record.Index == 0) break;
                    index = record.Index - 1;
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (records.Values.Any(r => (r.Reference & 0x0000ffffffffffffUL) >= 16 && r.HasAttributeList
                && (r.Names.Count == 0 || (!r.Directory && r.Links > r.Names.Count))))
                throw new NotSupportedException("NTFS extension records require directory enumeration for complete names.");
            ulong rootReference = root.TrimEnd('\\').Equals(drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? records.Values.Single(r => (r.Reference & 0x0000ffffffffffffUL) == 5).Reference
                : FileStorageInfo.Read(root).Identity.Low;
            if (!records.ContainsKey(rootReference)) throw new IOException("The selected directory was not found in the MFT.");
            buffer = BuildTree(records.Values, rootReference, serial, root, options, cancel, errors);
            return true;
        }
        // InvalidDataException und EndOfStreamException gehoeren dazu: Genau sie wirft der Parser
        // bei beschaedigten Records — ohne sie hier gaebe es keinen Rueckfall auf den
        // Verzeichnisscan, sondern einen abgebrochenen Scan.
        catch (Exception ex) when (ex is IOException or Win32Exception or NotSupportedException
            or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException
            or InvalidDataException or EndOfStreamException)
        {
            unavailable = ex.Message;
            return false;
        }
    }

    internal static NodeBuffer BuildTree(IEnumerable<NtfsFileRecord> records, ulong rootReference, ulong serial,
        string root, WalkOptions options, CancellationToken cancel, ScanErrorLog? errors = null)
    {
        var children = new Dictionary<ulong, List<(NtfsFileRecord Record, string Name)>>();
        foreach (NtfsFileRecord record in records)
        {
            if ((record.Reference & 0x0000ffffffffffffUL) < 16) continue; // internal NTFS metadata
            foreach (NtfsName name in record.Names)
            {
                if (!children.TryGetValue(name.Parent, out var list)) children[name.Parent] = list = [];
                list.Add((record, name.Name));
            }
        }
        var buffer = new NodeBuffer();
        var queue = new Queue<(ulong Reference, int Id, string Path)>();
        var seen = new HashSet<ulong> { rootReference };
        var exclusions = new ScanExclusions(options.ExclusionRoot ?? root, options.ExcludedPaths);
        queue.Enqueue((rootReference, 0, root));
        int next = 0;
        while (queue.TryDequeue(out var parent))
        {
            cancel.ThrowIfCancellationRequested();
            if (!children.TryGetValue(parent.Reference, out var entries)) continue;
            foreach (var (record, name) in entries)
            {
                string path = Path.Combine(parent.Path, name);
                if (exclusions.IsExcluded(path)) continue;
                EntryFlags flags = EntryFlags.None;
                if ((record.Attributes & FileAttributes.ReparsePoint) != 0) flags |= EntryFlags.ReparsePoint;
                if (((uint)record.Attributes & (0x1000 | 0x40000 | 0x400000)) != 0) flags |= EntryFlags.CloudPlaceholder;
                if (record.Directory)
                {
                    if (!seen.Add(record.Reference)) continue;
                    int id = ++next;
                    buffer.AddDirectory(parent.Id, id, name, record.Modified, flags | EntryFlags.Directory);
                    if ((flags & EntryFlags.ReparsePoint) == 0) queue.Enqueue((record.Reference, id, path));
                }
                else
                {
                    FileStorageInfo storage = new(record.Allocation, new FileIdentity(serial, record.Reference, 0), record.Links);
                    long size = record.Size;
                    if (record.HasAttributeList)
                    {
                        // Der Basis-Record traegt dann nicht alles; nachgelesen wird ueber das
                        // Dateisystem. Ist die Datei inzwischen weg oder gesperrt, bleibt der
                        // Eintrag mit den Werten aus dem Record — ein einzelner Fehlgriff darf
                        // nicht den ganzen Scan zu Fall bringen.
                        try
                        {
                            storage = FileStorageInfo.Read(path, record.Attributes);
                            if ((flags & (EntryFlags.ReparsePoint | EntryFlags.CloudPlaceholder)) == 0)
                                size = new FileInfo(path).Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                        {
                            flags |= EntryFlags.Unreadable;
                            errors?.Record(path, ex.HResult);
                        }
                    }
                    buffer.AddFile(parent.Id, name, size, record.Modified, flags, storage);
                }
            }
        }
        return buffer;
    }

    private static (ulong Index, byte[] Bytes) ReadRecord(NativeFileHandle volume, ulong index, int size)
    {
        byte[] input = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, index);
        // The native structure is padded to 16 bytes although FileRecordBuffer starts at 12.
        byte[] output = Control(volume, 0x90068, input, size + 16);
        if (output.Length < 12) throw new InvalidDataException("Truncated NTFS record response.");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)));
        if (length != size || output.Length < length + 12) throw new InvalidDataException("Unexpected NTFS record size.");
        return (BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000ffffffffffffUL, output.AsSpan(12, length).ToArray());
    }

    private static byte[] Control(NativeFileHandle volume, uint code, byte[] input, int size)
    {
        byte[] output = new byte[size];
        if (!DeviceIoControl(volume, code, input, input.Length, output, output.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return output.AsSpan(0, returned).ToArray();
    }

    private static void ReadExactly(NativeFileHandle volume, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(volume, buffer, offset);
            if (read == 0) throw new EndOfStreamException("Unexpected end of NTFS volume.");
            buffer = buffer[read..];
            offset += read;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern NativeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(NativeFileHandle volume, uint code, byte[] input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
