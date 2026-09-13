using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.IO.Compression;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;
using NativeFileHandle = Microsoft.Win32.SafeHandles.SafeFileHandle;

namespace DiskStats.Core.Tests;

public sealed class StorageAndFilterTests
{
    private static NodeStore Scan(TestTree tree, params string[] excluded)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 2, ExcludedPaths = excluded });
        NodeStore store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Hardlinks_belegen_nur_einmal_speicher_und_ueberstehen_snapshots()
    {
        using var tree = new TestTree();
        tree.AddFile("original.bin", 100_000);
        Assert.True(CreateHardLinkW(tree.PathOf("link.bin"), tree.PathOf("original.bin"), IntPtr.Zero));
        NodeStore store = Scan(tree);
        Assert.Equal(200_000, store.Size[0]);
        Assert.Equal(store.IdentityOf(1), store.IdentityOf(2));
        Assert.True(store.IdentityOf(1).IsKnown);
        Assert.Equal(2u, store.LinksOf(1));
        Assert.Equal(store.AllocationOf(1), store.PhysicalSize[0]);
        Assert.InRange(store.PhysicalSize[0], 100_000, 110_000);
        Assert.Equal(0, store.UnknownAllocation[0]);
        using var stream = new MemoryStream();
        SnapshotFormat.Write(store, stream);
        stream.Position = 0;
        NodeStore restored = SnapshotFormat.Read(stream);
        Assert.Equal(store.Allocation, restored.Allocation);
        Assert.Equal(store.FileIds, restored.FileIds);
        Assert.Equal(store.LinkCounts, restored.LinkCounts);
        Assert.Equal(store.PhysicalSize, restored.PhysicalSize);
    }

    [Fact]
    public void Sparse_datei_zeigt_logische_laenge_und_tatsaechliche_belegung()
    {
        using var tree = new TestTree();
        string path = tree.PathOf("sparse.bin");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            stream.SetLength(64L * 1024 * 1024);
        }
        NodeStore store = Scan(tree);
        Assert.Equal(64L * 1024 * 1024, store.Size[0]);
        Assert.InRange(store.PhysicalSize[0], 0, 4096);
        Assert.Equal(0, store.UnknownAllocation[0]);
    }

    [Fact]
    public void Fehlende_metadaten_werden_nicht_als_gemessene_null_ausgegeben()
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "unknown.bin", 123, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);
        Assert.Equal(-1, store.AllocationOf(1));
        Assert.Equal(1, store.UnknownAllocation[0]);
        Assert.Equal(FileStorageInfo.Unknown, FileStorageInfo.Read(@"C:\not-existing-diskstats-file"));
    }

    [Fact]
    public void Komprimierte_datei_wird_nach_tatsaechlicher_belegung_gemessen()
    {
        using var tree = new TestTree();
        string path = tree.PathOf("compressed.bin");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            file.Write(new byte[1024 * 1024]);
            file.Flush(flushToDisk: true);
            ushort format = 2;
            Assert.True(SetCompression(file.SafeFileHandle, 0x9c040, ref format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero));
        }
        NodeStore store = Scan(tree);
        Assert.Equal(1024 * 1024, store.Size[0]);
        Assert.InRange(store.PhysicalSize[0], 0, 128 * 1024);
        Assert.Equal(0, store.UnknownAllocation[0]);
    }

    [Fact]
    public void Hardlink_zuordnung_ist_unabhaengig_von_der_scanreihenfolge()
    {
        var buffer = new NodeBuffer();
        var storage = new FileStorageInfo(4096, new FileIdentity(1, 42, 0), 2);
        buffer.AddFile(0, "z.bin", 100, 0, EntryFlags.None, storage);
        buffer.AddFile(0, "a.bin", 100, 0, EntryFlags.None, storage);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);
        Assert.Equal(0, store.PhysicalSize[1]);
        Assert.Equal(4096, store.PhysicalSize[2]);
        DiskStats.Core.Cleanup.CleanupList.ApplyRemoval(store, 2);
        Assert.Equal(4096, store.PhysicalSize[0]);
        Assert.Equal(4096, store.PhysicalSize[1]);
    }

    [Fact]
    public void Snapshots_der_version_eins_bleiben_lesbar_mit_unbekannter_belegung()
    {
        using var tree = new TestTree();
        tree.AddFile("old.bin", 123);
        NodeStore store = Scan(tree);
        using var current = new MemoryStream();
        SnapshotFormat.Write(store, current);
        current.Position = 0;
        using var plain = new MemoryStream();
        using (var reader = new BrotliStream(current, CompressionMode.Decompress, leaveOpen: true)) reader.CopyTo(plain);
        byte[] legacy = plain.ToArray()[..^(36 * store.Count)];
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(4), 1);
        using var old = new MemoryStream();
        using (var writer = new BrotliStream(old, CompressionLevel.Fastest, leaveOpen: true)) writer.Write(legacy);
        old.Position = 0;
        NodeStore restored = SnapshotFormat.Read(old);
        Assert.Equal(123, restored.Size[0]);
        Assert.Single(NodeSearch.Find(restored, "old.bin"));
        Assert.Equal(1, restored.UnknownAllocation[0]);
    }

    [Fact]
    public void Ausschluesse_verhindern_den_scan_ganzer_teilbaeume()
    {
        using var tree = new TestTree();
        tree.AddFile("project/node_modules/ignored.bin", 900)
            .AddFile("project/cache/ignored.bin", 800).AddFile("project/code.cs", 100)
            .AddFile("other/keep.txt", 200).AddFile("ignored.log", 700);
        NodeStore store = Scan(tree, "node_modules", "project/cache", "*.log");
        Assert.Equal(300, store.Size[0]);
        Assert.Empty(NodeSearch.Find(store, "ignored"));
        Assert.Empty(NodeSearch.Find(store, "node_modules"));
    }

    [Fact]
    public void Filter_kombiniert_name_pfad_groesse_und_alter_ohne_trefferlimit()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 220; i++) tree.AddFile($"archive/report-{i}.log", 200);
        tree.AddFile("recent/report-new.log", 300).AddFile("archive/tiny.log", 1);
        foreach (string file in Directory.EnumerateFiles(tree.PathOf("archive")))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
        NodeStore store = Scan(tree);
        var query = new FileQuery { Name = "report-*.log", PathPattern = "archive/**", MinBytes = 100,
            MaxBytes = 250, OlderThanDays = 90 };
        Assert.Equal(220, query.Find(store).Count);
        Assert.Equal(220, (query with { UseRegex = true, Name = @"^report-\d+\.log$", PathPattern = "^archive/" }).Find(store).Count);
        Assert.Throws<ArgumentException>(() => (query with { MinBytes = 900 }).Find(store));
        Assert.ThrowsAny<ArgumentException>(() => (query with { UseRegex = true, Name = "[" }).Find(store));
    }

    /// <summary>
    /// Ein Nutzer-Regex wie "(a+)+$" liesse die Rueckwaertssuche bei passend boesen Namen
    /// explodieren. Mit linearer Auswertung bleibt es ein Muster — kein Timeout je Zeile,
    /// keine Sekunde je Datei.
    /// </summary>
    [Fact]
    public void Boesartiger_regex_laeuft_linear_statt_in_den_timeout()
    {
        var buffer = new NodeBuffer();
        for (int i = 0; i < 200; i++)
            buffer.AddFile(0, new string('a', 40) + "b" + i, 10, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);

        var query = new FileQuery { UseRegex = true, Name = "^(a+)+$" };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<int> found = query.Find(store);

        Assert.Empty(found);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Absurde_tageswerte_werden_abgelehnt_statt_ueberzulaufen()
    {
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "alt.txt", 10, DateTime.UtcNow.AddDays(-400).Ticks, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);

        Assert.Throws<ArgumentException>(() => new FileQuery { OlderThanDays = int.MaxValue }.Find(store));
        Assert.Throws<ArgumentException>(() => new FileQuery { OlderThanDays = -1 }.Find(store));
        // Das aelteste erlaubte Datum liegt vor jeder Datei: alles ist juenger, nichts trifft.
        Assert.Empty(new FileQuery { OlderThanDays = 3652058 }.Find(store));
        Assert.Single(new FileQuery { OlderThanDays = 365 }.Find(store));
    }

    /// <summary>
    /// Ein Pfad ausserhalb der Wurzel ist "..\etwas" relativ — und "**" in einem Muster
    /// wuerde ihn treffen. Was nicht unter der Wurzel liegt, ist nicht ausgeschlossen, es
    /// ist schlicht nicht Teil des Scans.
    /// </summary>
    [Fact]
    public void Ausschluesse_gelten_nur_unterhalb_der_wurzel()
    {
        var exclusions = new ScanExclusions(@"C:\root\scan", ["**/cache", "node_modules"]);

        Assert.True(exclusions.IsExcluded(@"C:\root\scan\a\cache"));
        Assert.True(exclusions.IsExcluded(@"C:\root\scan\node_modules"));
        Assert.False(exclusions.IsExcluded(@"C:\root\other\cache"));
        Assert.False(exclusions.IsExcluded(@"C:\root\scan\..\other\node_modules"));
        Assert.False(exclusions.IsExcluded(@"D:\cache"));
    }

    /// <summary>
    /// Leere Dateien werden nicht mehr geoeffnet. Sie duerfen deshalb nicht als "Belegung
    /// unbekannt" erscheinen — das waere ein Fragezeichen in jedem Ordner mit einer .gitkeep.
    /// </summary>
    [Fact]
    public void Leere_dateien_belegen_bekanntermassen_nichts()
    {
        using var tree = new TestTree();
        tree.AddFile("leer.txt", 0).AddFile("voll.txt", 10);
        NodeStore store = Scan(tree);

        int leer = Assert.Single(NodeSearch.Find(store, "leer.txt")).Node;
        Assert.Equal(0, store.AllocationOf(leer));
        Assert.Equal(0, store.UnknownAllocation[0]);
        Assert.Equal(1u, store.LinksOf(leer));
    }

    [Fact]
    public void Regex_mit_rueckbezug_bleibt_moeglich()
    {
        // Rueckbezuege kann die lineare Auswertung nicht; dann greift die klassische mit Zeitgrenze.
        var buffer = new NodeBuffer();
        buffer.AddFile(0, "abab.txt", 10, 0, EntryFlags.None);
        buffer.AddFile(0, "abcd.txt", 10, 0, EntryFlags.None);
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);

        Assert.Single(new FileQuery { UseRegex = true, Name = @"^(ab)\1\.txt$" }.Find(store));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(NativeFileHandle handle, uint code, IntPtr input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCompression(NativeFileHandle handle, uint code, ref ushort input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
