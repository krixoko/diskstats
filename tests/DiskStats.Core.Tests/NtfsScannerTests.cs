using System.Buffers.Binary;
using System.Text;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public sealed class NtfsScannerTests
{
    private static byte[] Record(bool nonresident)
    {
        byte[] b = new byte[1024];
        "FILE"u8.CopyTo(b);
        W16(b, 4, 48); W16(b, 6, 3); W16(b, 16, 7); W16(b, 18, 1);
        W16(b, 20, 56); W16(b, 22, 1);
        W32(b, 28, 1024);
        int offset = 56;
        W32(b, offset, 0x10); W32(b, offset + 4, 72); W32(b, offset + 16, 48); W16(b, offset + 20, 24);
        W64(b, offset + 32, (ulong)new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        offset += 72;
        byte[] name = Encoding.Unicode.GetBytes("report.bin");
        int nameLength = (24 + 66 + name.Length + 7) & ~7;
        W32(b, offset, 0x30); W32(b, offset + 4, (uint)nameLength);
        W32(b, offset + 16, (uint)(66 + name.Length)); W16(b, offset + 20, 24);
        W64(b, offset + 24, 5UL | (3UL << 48));
        b[offset + 24 + 64] = (byte)(name.Length / 2); b[offset + 24 + 65] = 1;
        name.CopyTo(b, offset + 24 + 66);
        offset += nameLength;
        W32(b, offset, 0x80);
        if (nonresident)
        {
            W32(b, offset + 4, 72); b[offset + 8] = 1;
            W16(b, offset + 32, 64); W64(b, offset + 24, 2);
            W64(b, offset + 40, 12288); W64(b, offset + 48, 9000);
            new byte[] { 0x11, 3, 10, 0 }.CopyTo(b, offset + 64);
            offset += 72;
        }
        else
        {
            W32(b, offset + 4, 32); W32(b, offset + 16, 4); W16(b, offset + 20, 24);
            "data"u8.CopyTo(b.AsSpan(offset + 24));
            offset += 32;
        }
        W32(b, offset, 0xffffffff); W32(b, 24, (uint)(offset + 8));
        W16(b, 48, 0xaaaa);
        W16(b, 50, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(510)));
        W16(b, 52, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(1022)));
        W16(b, 510, 0xaaaa); W16(b, 1022, 0xaaaa);
        return b;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parst_dateinamen_identitaet_groessen_und_zeit_aus_rohen_records(bool nonresident)
    {
        NtfsFileRecord record = Assert.IsType<NtfsFileRecord>(NtfsRecordParser.Parse(Record(nonresident), 42, 512));
        Assert.Equal(42UL | (7UL << 48), record.Reference);
        Assert.Equal("report.bin", Assert.Single(record.Names).Name);
        Assert.Equal(5UL | (3UL << 48), record.Names[0].Parent);
        Assert.Equal(nonresident ? 9000 : 4, record.Size);
        Assert.Equal(nonresident ? 12288 : 0, record.Allocation);
        Assert.Equal(new DateTime(2020, 1, 2).Ticks, record.Modified);
        Assert.False(record.Directory);
        if (nonresident) Assert.Equal(new NtfsDataRun(0, 10, 3), Assert.Single(record.DataRuns));
    }

    [Fact]
    public void Abgerissene_records_und_ungueltige_attribute_werden_abgelehnt()
    {
        byte[] b = Record(false);
        b[510] = 0;
        Assert.Throws<InvalidDataException>(() => NtfsRecordParser.Parse(b, 42, 512));
        b = Record(false);
        W32(b, 56 + 4, 5000);
        Assert.Throws<InvalidDataException>(() => NtfsRecordParser.Parse(b, 42, 512));
    }

    [Fact]
    public void Datenlaeufe_unterstuetzen_negative_offsets_und_sparse_bereiche()
    {
        var runs = NtfsRecordParser.ParseRuns([0x11, 3, 10, 0x11, 2, 0xfe, 0x01, 5, 0]);
        Assert.Equal(new[] { new NtfsDataRun(0, 10, 3), new NtfsDataRun(3, 8, 2), new NtfsDataRun(5, -1, 5) }, runs);
        Assert.Throws<InvalidDataException>(() => NtfsRecordParser.ParseRuns([0x11, 0, 1, 0]));
        Assert.Throws<InvalidDataException>(() => NtfsRecordParser.ParseRuns([0x88, 1]));
    }

    [Fact]
    public void Mft_baum_beachtet_hardlinks_ausschluesse_und_junctions()
    {
        NtfsFileRecord[] records =
        [
            new(16, true, FileAttributes.Directory, 0, 0, 0, 1, [new(5, "folder")], [], false),
            new(17, false, FileAttributes.Normal, 100, 4096, 0, 2, [new(16, "a.bin"), new(16, "b.bin")], [], false),
            new(18, true, FileAttributes.Directory | FileAttributes.ReparsePoint, 0, 0, 0, 1, [new(5, "junction")], [], false),
            new(19, false, FileAttributes.Normal, 900, 4096, 0, 1, [new(18, "not-followed.bin")], [], false),
            new(20, false, FileAttributes.Normal, 800, 4096, 0, 1, [new(16, "ignored.log")], [], false),
        ];
        NodeBuffer buffer = NtfsScanner.BuildTree(records, 5, 123, @"C:\fixture", new WalkOptions { ExcludedPaths = ["*.log"] }, default);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);
        Assert.Equal(200, store.Size[0]);
        Assert.Equal(4096, store.PhysicalSize[0]);
        Assert.Empty(NodeSearch.Find(store, "not-followed"));
        Assert.Empty(NodeSearch.Find(store, "ignored"));
        Assert.Equal(5, store.Count);
    }

    /// <summary>
    /// Records mit Attributliste werden ueber das Dateisystem nachgelesen. Ist die Datei
    /// inzwischen weg oder gesperrt, darf das nicht den ganzen MFT-Scan zu Fall bringen —
    /// der Eintrag bekommt die Groesse aus dem Record und die Markierung "nicht lesbar".
    /// </summary>
    [Fact]
    public void Mft_baum_ueberlebt_eine_nicht_nachlesbare_datei()
    {
        NtfsFileRecord[] records =
        [
            new(16, true, FileAttributes.Directory, 0, 0, 0, 1, [new(5, "folder")], [], false),
            new(17, false, FileAttributes.Normal, 100, 4096, 0, 1, [new(16, "ok.bin")], [], false),
            new(18, false, FileAttributes.Normal, 700, 4096, 0, 1, [new(16, "gone.bin")], [], HasAttributeList: true),
        ];
        NodeBuffer buffer = NtfsScanner.BuildTree(records, 5, 123, @"C:\diskstats-gibt-es-nicht", new WalkOptions(), default);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\diskstats-gibt-es-nicht");
        SizeAggregator.Aggregate(store);

        Assert.Equal(4, store.Count);
        int gone = Enumerable.Range(1, store.Count - 1).Single(i => store.GetName(i) == "gone.bin");
        Assert.Equal(700, store.Size[gone]);
        Assert.True((store.Flags[gone] & EntryFlags.Unreadable) != 0);
        Assert.Equal(800, store.Size[0]);
    }

    [Fact]
    public void Ntfs_auswahl_liefert_auch_ohne_volume_rechte_einen_vollstaendigen_scan()
    {
        using var tree = new TestTree();
        tree.AddFile("kept.bin", 123).AddFile("skip/data.bin", 900);
        ScanCoordinator.Result result = ScanCoordinator.Scan(tree.Root, new NodeBuffer(),
            new WalkOptions { Method = ScanMethod.Ntfs, ExcludedPaths = ["skip"] });
        NodeStore store = NodeStoreBuilder.Build(result.Buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        Assert.Equal(123, store.Size[0]);
        Assert.Single(NodeSearch.Find(store, "kept"));
        if (result.Method == ScanMethod.Directory) Assert.False(string.IsNullOrWhiteSpace(result.Fallback));
        else Assert.Null(result.Fallback);
    }

    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
}
