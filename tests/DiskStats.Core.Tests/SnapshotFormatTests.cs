using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.Core.Tests;

public class SnapshotFormatTests
{
    private static NodeStore BuildStore(TestTree tree)
    {
        var buffer = new NodeBuffer();
        DirectoryWalker.Walk(tree.Root, buffer, new WalkOptions { WorkerCount = 4 });
        var store = NodeStoreBuilder.Build(buffer, tree.Root);
        SizeAggregator.Aggregate(store);
        return store;
    }

    [Fact]
    public void Roundtrip_erhaelt_struktur_groessen_und_namen()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 100).AddFile("sub/b.bin", 200).AddFile("sub/Größe 📊.bin", 300);

        NodeStore original = BuildStore(tree);

        using var stream = new MemoryStream();
        SnapshotFormat.Write(original, stream);
        stream.Position = 0;
        NodeStore restored = SnapshotFormat.Read(stream);

        Assert.Equal(original.Count, restored.Count);
        Assert.Equal(original.RootPath, restored.RootPath);
        Assert.Equal(original.Size, restored.Size);
        Assert.Equal(original.ParentIndex, restored.ParentIndex);
        Assert.Equal(original.ChildStart, restored.ChildStart);
        Assert.Equal(original.ChildCount, restored.ChildCount);
        Assert.Equal(original.Flags, restored.Flags);

        for (int i = 0; i < original.Count; i++)
            Assert.Equal(original.GetName(i), restored.GetName(i));
    }

    [Fact]
    public void Snapshot_ist_deutlich_kleiner_als_die_rohen_arrays()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 300; i++)
            tree.AddFile($"ordner{i % 10}/datei-{i}.bin", 1024);

        NodeStore store = BuildStore(tree);

        using var stream = new MemoryStream();
        SnapshotFormat.Write(store, stream);

        // Pro Knoten geschrieben: 4+4+4+4 (int) + 2 (ushort) + 8+8 (long) + 1 (flags) = 35 Byte.
        // Version 2 adds allocation (8), identity (24), and link count (4).
        long uncompressed = store.Count * 71L + store.Names.ByteLength;
        Assert.True(stream.Length < uncompressed,
            $"Snapshot {stream.Length} B war nicht kleiner als unkomprimiert {uncompressed} B");
    }

    [Fact]
    public void Fremde_datei_wird_abgelehnt()
    {
        using var stream = new MemoryStream("kein snapshot"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => SnapshotFormat.Read(stream));
    }

    // ---------- Vertrauensgrenze: Der Dateikopf ist Eingabe, keine Wahrheit ----------

    /// <summary>Ein Kopf mit frei waehlbaren Zahlen, sonst wie ein echter Snapshot.</summary>
    private static MemoryStream HeaderOnly(int nodeCount, int poolLength)
    {
        var stream = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(stream,
                   System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(brotli, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("DSKS"u8);
            w.Write(2);
            w.Write(DateTime.UtcNow.Ticks);
            w.Write(@"C:\irgendwo");
            w.Write(nodeCount);
            w.Write(poolLength);
        }
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData(int.MaxValue, 10)]
    [InlineData(-1, 10)]
    [InlineData(0, 10)]
    [InlineData(10, int.MaxValue)]
    [InlineData(10, -1)]
    public void Unplausible_kopfwerte_werden_abgelehnt_statt_speicher_zu_reservieren(int nodes, int pool)
    {
        using MemoryStream stream = HeaderOnly(nodes, pool);

        // Nicht OutOfMemory, nicht Overflow, nicht EndOfStream — eine klare Diagnose.
        Assert.Throws<InvalidDataException>(() => SnapshotFormat.Read(stream));
    }

    private static NodeStore Clone(NodeStore s, Action<NodeStore> mutate, byte[]? pool = null)
    {
        var copy = new NodeStore
        {
            RootPath = s.RootPath,
            Count = s.Count,
            ParentIndex = (int[])s.ParentIndex.Clone(),
            ChildStart = (int[])s.ChildStart.Clone(),
            ChildCount = (int[])s.ChildCount.Clone(),
            NameOffset = (int[])s.NameOffset.Clone(),
            NameLength = (ushort[])s.NameLength.Clone(),
            Size = (long[])s.Size.Clone(),
            MTime = (long[])s.MTime.Clone(),
            Flags = (EntryFlags[])s.Flags.Clone(),
            Names = NamePool.FromBytes(pool ?? s.Names.ToArray()),
            Allocation = (long[])s.Allocation.Clone(),
            FileIds = (FileIdentity[])s.FileIds.Clone(),
            LinkCounts = (uint[])s.LinkCounts.Clone(),
        };
        mutate(copy);
        return copy;
    }

    private static void AssertRejected(NodeStore broken)
    {
        using var stream = new MemoryStream();
        SnapshotFormat.Write(broken, stream);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => SnapshotFormat.Read(stream));
    }

    [Fact]
    public void Kind_das_vor_seinem_elternteil_liegt_wird_abgelehnt()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1).AddFile("sub/b.bin", 2);
        NodeStore store = BuildStore(tree);

        AssertRejected(Clone(store, s => s.ParentIndex[1] = 5));
        AssertRejected(Clone(store, s => s.ParentIndex[0] = 0));
    }

    [Fact]
    public void Kinderbereich_ausserhalb_des_baums_wird_abgelehnt()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1).AddFile("sub/b.bin", 2);
        NodeStore store = BuildStore(tree);

        AssertRejected(Clone(store, s => s.ChildCount[0] = 1000));
        AssertRejected(Clone(store, s => s.ChildStart[0] = -3));
    }

    [Fact]
    public void Name_ausserhalb_des_namensvorrats_wird_abgelehnt()
    {
        using var tree = new TestTree();
        tree.AddFile("a.bin", 1);
        NodeStore store = BuildStore(tree);

        AssertRejected(Clone(store, s => s.NameOffset[1] = s.Names.ByteLength));
        AssertRejected(Clone(store, s => s.NameLength[1] = ushort.MaxValue));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    public void Name_der_den_pfad_verlaesst_wird_abgelehnt(string name)
    {
        // Ein Snapshot mit einem Knoten namens ".." liesse PathOf aus der Scanwurzel hinaus —
        // und damit die Aufraeumliste auf etwas zeigen, das nie gescannt wurde.
        using var tree = new TestTree();
        tree.AddFile("abcdef.bin", 1);
        NodeStore store = BuildStore(tree);

        byte[] pool = store.Names.ToArray();
        byte[] evil = System.Text.Encoding.UTF8.GetBytes(name);
        evil.CopyTo(pool, store.NameOffset[1]);

        AssertRejected(Clone(store, s => s.NameLength[1] = (ushort)evil.Length, pool));
    }

    [Fact]
    public void Zufaellig_beschaedigte_snapshots_enden_nur_mit_der_erwarteten_ausnahme()
    {
        using var tree = new TestTree();
        for (int i = 0; i < 40; i++) tree.AddFile($"o{i % 4}/d{i}.bin", 10 + i);
        NodeStore store = BuildStore(tree);

        using var clean = new MemoryStream();
        SnapshotFormat.Write(store, clean);
        byte[] original = clean.ToArray();

        var random = new Random(20260908);
        for (int round = 0; round < 300; round++)
        {
            byte[] damaged = (byte[])original.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++)
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));

            try
            {
                using var stream = new MemoryStream(damaged);
                NodeStore restored = SnapshotFormat.Read(stream);
                // Wenn es durchgeht, muss das Ergebnis wenigstens begehbar sein.
                for (int i = 0; i < restored.Count; i++) _ = restored.PathOf(i);
            }
            catch (InvalidDataException) { }
        }
    }
}
