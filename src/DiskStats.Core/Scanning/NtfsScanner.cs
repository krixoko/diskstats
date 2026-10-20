using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using NativeFileHandle = Microsoft.Win32.SafeHandles.SafeFileHandle;
using DiskStats.Core.Interop;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Scanning;

/// <summary>Reads the MFT in sequential blocks from a read-only volume handle.</summary>
public static class NtfsScanner
{
    // DeviceIoControl-Codes, aus winioctl.h.
    private const uint FsctlGetNtfsVolumeData = 0x90064;
    private const uint FsctlGetNtfsFileRecord = 0x90068;

    /// <summary>Referenznummer des Wurzelverzeichnisses und der ersten Nutzer-Records.</summary>
    private const ulong RootRecordNumber = 5;
    private const ulong FirstUserRecordNumber = 16;
    private const ulong RecordNumberMask = 0x0000ffffffffffffUL;

    /// <summary>
    /// Anteil des Fortschrittsbalkens, der auf das Lesen der MFT entfaellt. Der Rest gehoert dem
    /// Aufbau des Baums — der dauert bei Millionen Eintraegen spuerbar, aber kuerzer als das Lesen.
    /// </summary>
    private const double ReadShare = 0.8;

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
        var progress = new ScanProgress(options.MftProgress);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new NotSupportedException("NTFS scanning requires Windows.");
            root = Path.GetFullPath(root);
            string drive = Path.GetPathRoot(root)!;
            if (drive.Length != 3 || new DriveInfo(drive).DriveFormat != "NTFS")
                throw new NotSupportedException("Direct scanning requires a local NTFS volume.");
            using NativeFileHandle volume = NativeMethods.CreateFileW(@"\\.\" + drive[..2],
                NativeMethods.GenericRead, NativeMethods.ShareAll, IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            Geometry geometry = ReadGeometry(volume);
            NtfsFileRecord mft = NtfsRecordParser.Parse(ReadRecord(volume, 0, geometry.RecordSize).Bytes, 0, geometry.Sector)
                ?? throw new InvalidDataException("MFT record unavailable.");

            Dictionary<ulong, NtfsFileRecord> records = ReadAllRecords(volume, geometry, mft, cancel, errors, progress);
            cancel.ThrowIfCancellationRequested();

            RequireCompleteNames(records);
            ulong rootReference = root.TrimEnd('\\').Equals(drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? records.Values.Single(r => (r.Reference & RecordNumberMask) == RootRecordNumber).Reference
                : FileStorageInfo.Read(root).Identity.Low;
            if (!records.ContainsKey(rootReference)) throw new IOException("The selected directory was not found in the MFT.");

            buffer = BuildTree(records.Values, rootReference, geometry.Serial, root, options, cancel, errors, progress);
            progress.Report(1);
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

    /// <summary>Was das Dateisystem ueber sich verraet und was zum Lesen der MFT noetig ist.</summary>
    private readonly record struct Geometry(ulong Serial, int Sector, int Cluster, int RecordSize, long ValidLength);

    private static Geometry ReadGeometry(NativeFileHandle volume)
    {
        byte[] data = Control(volume, FsctlGetNtfsVolumeData, [], 128);
        if (data.Length < 96) throw new InvalidDataException("Truncated NTFS volume information.");
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(data);
        int sector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40)));
        int cluster = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44)));
        int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48)));
        long validLength = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56));
        if (recordSize < 512 || recordSize > 65536 || cluster < 512 || validLength < recordSize)
            throw new InvalidDataException("Invalid NTFS geometry.");
        return new Geometry(serial, sector, cluster, recordSize, validLength);
    }

    /// <summary>
    /// Liest alle Records der MFT. Der schnelle Weg liest sie blockweise; er setzt voraus, dass der
    /// $MFT-Record die ganze Tabelle lueckenlos beschreibt. Sonst bleibt der langsame Weg: Das
    /// Dateisystem liefert jeden Record einzeln und setzt fragmentierte Attributlisten selbst zusammen.
    /// </summary>
    private static Dictionary<ulong, NtfsFileRecord> ReadAllRecords(NativeFileHandle volume, Geometry g,
        NtfsFileRecord mft, CancellationToken cancel, ScanErrorLog errors, ScanProgress progress)
    {
        long expected = g.ValidLength / g.RecordSize;

        // Vorab dimensioniert, damit das Wachsen nicht kurzzeitig den doppelten Speicher braucht.
        var records = new Dictionary<ulong, NtfsFileRecord>((int)Math.Min(expected, 4_000_000));

        long covered = 0;
        foreach (NtfsDataRun run in mft.DataRuns.OrderBy(r => r.Vcn))
        {
            if (run.Lcn < 0 || checked(run.Vcn * g.Cluster) != covered) break;
            covered = checked(covered + run.Clusters * g.Cluster);
        }

        if (covered >= g.ValidLength && g.Cluster % g.RecordSize == 0)
            ReadMftInBlocks(volume, g, mft, records, cancel, errors, progress);
        else
            ReadMftRecordByRecord(volume, g, records, cancel, progress);

        return records;
    }

    private static void ReadMftInBlocks(NativeFileHandle volume, Geometry g, NtfsFileRecord mft,
        Dictionary<ulong, NtfsFileRecord> records, CancellationToken cancel, ScanErrorLog errors, ScanProgress progress)
    {
        int damaged = 0;
        byte[] block = new byte[Math.Max(1024 * 1024, g.RecordSize)];
        foreach (NtfsDataRun run in mft.DataRuns.OrderBy(r => r.Vcn))
        {
            long logical = checked(run.Vcn * g.Cluster);
            long length = Math.Min(checked(run.Clusters * g.Cluster), g.ValidLength - logical);
            for (long offset = 0; offset < length;)
            {
                cancel.ThrowIfCancellationRequested();
                int take = (int)Math.Min(block.Length, length - offset);
                take -= take % g.RecordSize;
                if (take == 0) break;
                ReadExactly(volume, block.AsSpan(0, take), checked(run.Lcn * g.Cluster + offset));
                for (int p = 0; p < take; p += g.RecordSize)
                {
                    ulong index = (ulong)((logical + offset + p) / g.RecordSize);
                    // Ein einzelner zerrissener Record (gerade im Schreiben, oder BAAD)
                    // kostet einen Eintrag, nicht den Scan. Haeufen sie sich, stimmt
                    // etwas Grundsaetzliches nicht — dann uebernimmt der Verzeichnisscan.
                    NtfsFileRecord? record;
                    try { record = NtfsRecordParser.Parse(block.AsSpan(p, g.RecordSize), index, g.Sector, keepDataRuns: false); }
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
                progress.Report(ReadShare * (logical + offset) / g.ValidLength);
            }
            if (logical + length >= g.ValidLength) break;
        }
    }

    private static void ReadMftRecordByRecord(NativeFileHandle volume, Geometry g,
        Dictionary<ulong, NtfsFileRecord> records, CancellationToken cancel, ScanProgress progress)
    {
        // Fragmented MFT attribute lists may not fit in the base record. The filesystem
        // can retrieve each complete FILE record without interpreting those extents here.
        ulong last = checked((ulong)(g.ValidLength / g.RecordSize - 1));
        ulong index = last;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var record = ReadRecord(volume, index, g.RecordSize);
            if (record.Index > index) throw new InvalidDataException("Unexpected NTFS record order.");
            NtfsFileRecord? parsed = NtfsRecordParser.Parse(record.Bytes, record.Index, g.Sector, keepDataRuns: false);
            if (parsed is not null) records[parsed.Reference] = parsed;
            if (record.Index == 0) break;
            index = record.Index - 1;
            // Rueckwaerts gelesen: je kleiner der Index, desto weiter ist man.
            if (last > 0) progress.Report(ReadShare * (last - index) / last);
        }
    }

    /// <summary>
    /// Records mit Attributliste, deren Namen oder Verlinkungen im Basis-Record fehlen, lassen sich
    /// hier nicht vollstaendig aufloesen — dann muss der Verzeichnisscan ran.
    /// </summary>
    private static void RequireCompleteNames(Dictionary<ulong, NtfsFileRecord> records)
    {
        if (records.Values.Any(r => (r.Reference & RecordNumberMask) >= FirstUserRecordNumber && r.HasAttributeList
            && (r.Names.Count == 0 || (!r.Directory && r.Links > r.Names.Count))))
            throw new NotSupportedException("NTFS extension records require directory enumeration for complete names.");
    }

    internal static NodeBuffer BuildTree(IEnumerable<NtfsFileRecord> records, ulong rootReference, ulong serial,
        string root, WalkOptions options, CancellationToken cancel, ScanErrorLog? errors = null,
        ScanProgress? progress = null)
    {
        var children = new Dictionary<ulong, List<(NtfsFileRecord Record, string Name)>>();
        foreach (NtfsFileRecord record in records)
        {
            if ((record.Reference & RecordNumberMask) < FirstUserRecordNumber) continue; // internal NTFS metadata
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
        int visited = 0;
        while (queue.TryDequeue(out var parent))
        {
            cancel.ThrowIfCancellationRequested();
            visited++;
            // Nur eine Naehrung: "children" kennt Ordner mit Inhalt, die Schlange auch leere und
            // nur die unterhalb der Wurzel. Der Balken kann daher vor dem Ende 100 % erreichen oder
            // knapp darunter bleiben — ScanProgress begrenzt ihn, TryScan meldet am Ende 100 %.
            if (progress is not null && children.Count > 0)
                progress.Report(ReadShare + (1 - ReadShare) * visited / children.Count);
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
        byte[] output = Control(volume, FsctlGetNtfsFileRecord, input, size + 16);
        if (output.Length < 12) throw new InvalidDataException("Truncated NTFS record response.");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)));
        if (length != size || output.Length < length + 12) throw new InvalidDataException("Unexpected NTFS record size.");
        return (BinaryPrimitives.ReadUInt64LittleEndian(output) & RecordNumberMask, output.AsSpan(12, length).ToArray());
    }

    private static byte[] Control(NativeFileHandle volume, uint code, byte[] input, int size)
    {
        byte[] output = new byte[size];
        if (!NativeMethods.DeviceIoControl(volume, code, input, input.Length, output, output.Length, out int returned, IntPtr.Zero))
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
}
